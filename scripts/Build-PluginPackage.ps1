[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$HostPath,
    [switch]$UpdateContractSnapshot
)

$ErrorActionPreference = 'Stop'

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Remove-DirectoryBestEffort {
    param([Parameter(Mandatory)][string]$Path)

    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            if (Test-Path $Path) {
                Remove-Item $Path -Recurse -Force -ErrorAction Stop
            }
            return
        }
        catch {
            if ($attempt -eq 8) {
                throw
            }
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $repoRoot 'plugin'
$snapshotPath = Join-Path $sourceRoot 'contract\mcp-contract.json'
$exportScript = Join-Path $PSScriptRoot 'Export-McpContract.ps1'
$verifyScript = Join-Path $PSScriptRoot 'Verify-PluginPackage.ps1'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\plugin'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$sourceManifestPath = Join-Path $sourceRoot 'plugin.json'
if (-not (Test-Path $sourceManifestPath)) {
    throw "Falta fuente canónica del plugin: $sourceManifestPath"
}

$sourceManifest = Get-Content $sourceManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$sourceManifest.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'plugin/plugin.json no declara version.'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'LoomLCI.PluginBuild.' + [Guid]::NewGuid().ToString('N'))
$currentContract = Join-Path $tempRoot 'mcp-contract.json'
$publishedHostRoot = Join-Path $tempRoot 'host'
$stageRoot = Join-Path $tempRoot 'package'

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    if ([string]::IsNullOrWhiteSpace($HostPath)) {
        New-Item -ItemType Directory -Path $publishedHostRoot -Force | Out-Null
        Write-Host 'Publishing Release Host for MCP contract export...'

        & dotnet publish (
            Join-Path $repoRoot 'src\LoomLCI.Host\LoomLCI.Host.csproj'
        ) -c Release -r win-x64 --self-contained true -o $publishedHostRoot

        if ($LASTEXITCODE -ne 0) {
            throw 'Falló publish de LoomLCI.Host para exportar el contrato MCP.'
        }

        $HostPath = Join-Path $publishedHostRoot 'LoomLCI.Host.exe'
    }

    $HostPath = [System.IO.Path]::GetFullPath($HostPath)
    if (-not (Test-Path $HostPath -PathType Leaf)) {
        throw "No existe HostPath: $HostPath"
    }

    $exportArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $exportScript,
        '-HostPath', $HostPath,
        '-OutputPath', $currentContract
    )
    & powershell.exe @exportArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Falló Export-McpContract.ps1.'
    }

    if ($UpdateContractSnapshot) {
        $snapshotParent = Split-Path -Parent $snapshotPath
        New-Item -ItemType Directory -Path $snapshotParent -Force | Out-Null
        [System.IO.File]::Copy($currentContract, $snapshotPath, $true)
        Write-Host "Updated committed MCP snapshot: $snapshotPath"
    }
    else {
        if (-not (Test-Path $snapshotPath -PathType Leaf)) {
            throw 'Falta plugin/contract/mcp-contract.json. Ejecute el build una vez con -UpdateContractSnapshot después de revisar el contrato.'
        }

        $expected = [System.IO.File]::ReadAllText($snapshotPath)
        $actual = [System.IO.File]::ReadAllText($currentContract)
        if ($expected -ne $actual) {
            throw 'El contrato MCP vivo difiere del snapshot del plugin. Revise el diff y ejecute con -UpdateContractSnapshot sólo si el cambio es intencional.'
        }

        Write-Host 'MCP contract snapshot matches the Release Host.'
    }

    $verifySourceArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $verifyScript,
        '-PluginRoot', $sourceRoot,
        '-ContractPath', $snapshotPath
    )
    & powershell.exe @verifySourceArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Falló la verificación de la fuente canónica del plugin.'
    }

    New-Item -ItemType Directory -Path (Join-Path $stageRoot 'skills\loomlci') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stageRoot '.codex-plugin') -Force | Out-Null

    Copy-Item (Join-Path $sourceRoot 'README.md') (Join-Path $stageRoot 'README.md') -Force
    Copy-Item (Join-Path $sourceRoot 'skills\loomlci\SKILL.md') (
        Join-Path $stageRoot 'skills\loomlci\SKILL.md') -Force

    $manifestJson = $sourceManifest | ConvertTo-Json -Depth 50
    Write-Utf8NoBom -Path (Join-Path $stageRoot 'plugin.json') -Text (
        $manifestJson + [Environment]::NewLine)

    $compatManifest = [ordered]@{
        interface = $sourceManifest.extensions.'com.openai'.interface
        name = $sourceManifest.name
        version = $sourceManifest.version
        description = $sourceManifest.description
        author = $sourceManifest.author
        keywords = $sourceManifest.keywords
        skills = './skills'
    }
    Write-Utf8NoBom -Path (
        Join-Path $stageRoot '.codex-plugin\plugin.json'
    ) -Text (
        ($compatManifest | ConvertTo-Json -Depth 50) + [Environment]::NewLine)

    $neutralPortableMcp = [ordered]@{
        '$schema' = 'https://agent-plugins.org/schemas/1.0.0/mcp.schema.json'
        mcpServers = [ordered]@{}
    }
    Write-Utf8NoBom -Path (Join-Path $stageRoot 'mcp.json') -Text (
        ($neutralPortableMcp | ConvertTo-Json -Depth 20) + [Environment]::NewLine)

    $neutralCompatMcp = [ordered]@{
        mcpServers = [ordered]@{}
    }
    Write-Utf8NoBom -Path (Join-Path $stageRoot '.mcp.json') -Text (
        ($neutralCompatMcp | ConvertTo-Json -Depth 20) + [Environment]::NewLine)

    $verifyPackageArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $verifyScript,
        '-PluginRoot', $stageRoot,
        '-ContractPath', $snapshotPath,
        '-RequireGeneratedCompatibility'
    )
    & powershell.exe @verifyPackageArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Falló la verificación del paquete generado.'
    }

    $packageDir = Join-Path $OutputRoot "LoomLCI-plugin-$version"
    $zipPath = "$packageDir.zip"

    Remove-DirectoryBestEffort -Path $packageDir
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Copy-Item $stageRoot $packageDir -Recurse -Force

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open(
        $zipPath,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem $packageDir -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($packageDir.Length).TrimStart('\', '/')
            $entryName = $relative.Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $_.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    $checkArchive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $unsafeEntries = @(
            $checkArchive.Entries |
                Where-Object { $_.FullName.Contains('\') } |
                ForEach-Object FullName
        )
        if ($unsafeEntries.Count -gt 0) {
            throw "El ZIP contiene entry names no portables: $($unsafeEntries -join ', ')."
        }
    }
    finally {
        $checkArchive.Dispose()
    }

    Write-Host "Plugin package: $zipPath"
    Write-Host "Plugin version: $version"
}
finally {
    Remove-DirectoryBestEffort -Path $tempRoot
}
