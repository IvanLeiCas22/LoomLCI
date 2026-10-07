[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PluginRoot,

    [Parameter(Mandatory)]
    [string]$ContractPath,

    [switch]$RequireGeneratedCompatibility
)

$ErrorActionPreference = 'Stop'

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Falta archivo requerido: $Path"
    }

    return Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Assert-NoLegacyPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    $forbidden = @(
        'C:\Users\',
        'bin\\Debug',
        'LoomLCI.Host.dll',
        'Documents\\ProyectosPersonales\\LoomLCI\\src'
    )

    foreach ($needle in $forbidden) {
        if ($Text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Metadata obsoleta en '$Path': contiene '$needle'."
        }
    }
}

$PluginRoot = [System.IO.Path]::GetFullPath($PluginRoot)
$ContractPath = [System.IO.Path]::GetFullPath($ContractPath)

$manifestPath = Join-Path $PluginRoot 'plugin.json'
$readmePath = Join-Path $PluginRoot 'README.md'
$skillPath = Join-Path $PluginRoot 'skills\loomlci\SKILL.md'

$manifest = Read-JsonFile $manifestPath
$contract = Read-JsonFile $ContractPath

if ($manifest.name -ne 'loomlci') {
    throw "plugin.json debe declarar name='loomlci'."
}
if ([string]::IsNullOrWhiteSpace($manifest.version) -or
    $manifest.version -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') {
    throw "Versión de plugin inválida: '$($manifest.version)'."
}
if ([string]::IsNullOrWhiteSpace($manifest.description)) {
    throw 'plugin.json debe tener description.'
}
if ($manifest.PSObject.Properties.Name -contains 'mcpServers') {
    throw 'plugin.json no debe declarar mcpServers; LoomLCI MCP + Tunnel es la conexión canónica.'
}
if ($null -eq $manifest.extensions.'com.openai'.interface) {
    throw 'plugin.json debe declarar extensions.com.openai.interface.'
}

$tools = @($contract.tools)
if ($tools.Count -eq 0) {
    throw 'El snapshot MCP no contiene tools.'
}

$toolNames = @($tools | ForEach-Object { $_.name })
$duplicates = @(
    $toolNames |
        Group-Object |
        Where-Object Count -gt 1 |
        ForEach-Object Name
)
if ($duplicates.Count -gt 0) {
    throw "El snapshot MCP contiene tool names duplicados: $($duplicates -join ', ')."
}

$toolSet = @{}
foreach ($name in $toolNames) {
    $toolSet[$name] = $true
}

$sourceFiles = @($manifestPath, $readmePath, $skillPath)
foreach ($path in $sourceFiles) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Falta archivo requerido: $path"
    }

    $text = Get-Content $path -Raw -Encoding UTF8
    Assert-NoLegacyPath -Path $path -Text $text
}

$skillText = Get-Content $skillPath -Raw -Encoding UTF8
$skillRefs = @(
    [regex]::Matches(
        $skillText,
        '\b(?:work|filesystem|process|python|computer)_[a-z0-9_]+\b') |
        ForEach-Object Value |
        Sort-Object -Unique
)

$unknownRefs = @($skillRefs | Where-Object { -not $toolSet.ContainsKey($_) })
if ($unknownRefs.Count -gt 0) {
    throw "La skill referencia tools ausentes del MCP vivo: $($unknownRefs -join ', ')."
}

$requiredPythonWorkflowMarkers = @(
    'python_execute',
    'python_packages_prepare',
    'python_reset',
    'loom.fs',
    'loom.process',
    'loom.display_image',
    'filesystem_view_image',
    'filesystem_render_pdf_page',
    'completamente en memoria',
    'imagen local',
    'MCP/tunnel',
    'Process top-level',
    'Independent'
)

$missingPythonWorkflowMarkers = @(
    $requiredPythonWorkflowMarkers |
        Where-Object {
            $skillText.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -lt 0
        }
)
if ($missingPythonWorkflowMarkers.Count -gt 0) {
    throw "La skill perdió guidance requerido del workflow Python final: $($missingPythonWorkflowMarkers -join ', ')."
}

if ($RequireGeneratedCompatibility) {
    $compatPath = Join-Path $PluginRoot '.codex-plugin\plugin.json'
    $portableMcpPath = Join-Path $PluginRoot 'mcp.json'
    $compatMcpPath = Join-Path $PluginRoot '.mcp.json'

    $compat = Read-JsonFile $compatPath
    $portableMcp = Read-JsonFile $portableMcpPath
    $compatMcp = Read-JsonFile $compatMcpPath

    if ($compat.name -ne $manifest.name -or
        $compat.version -ne $manifest.version -or
        $compat.description -ne $manifest.description) {
        throw 'El manifest de compatibilidad no coincide con plugin.json.'
    }

    if ($compat.PSObject.Properties.Name -contains 'mcpServers') {
        throw '.codex-plugin/plugin.json no debe declarar mcpServers.'
    }

    $rootInterface = $manifest.extensions.'com.openai'.interface |
        ConvertTo-Json -Depth 50 -Compress
    $compatInterface = $compat.interface |
        ConvertTo-Json -Depth 50 -Compress
    if ($rootInterface -ne $compatInterface) {
        throw 'La interface del manifest de compatibilidad no coincide con plugin.json.'
    }

    if ($null -eq $portableMcp.mcpServers -or
        @($portableMcp.mcpServers.PSObject.Properties).Count -ne 0) {
        throw 'mcp.json debe neutralizar el wiring histórico con mcpServers vacío.'
    }
    if ($null -eq $compatMcp.mcpServers -or
        @($compatMcp.mcpServers.PSObject.Properties).Count -ne 0) {
        throw '.mcp.json debe neutralizar el wiring histórico con mcpServers vacío.'
    }

    foreach ($path in @($compatPath, $portableMcpPath, $compatMcpPath)) {
        $text = Get-Content $path -Raw -Encoding UTF8
        Assert-NoLegacyPath -Path $path -Text $text
    }
}

Write-Host "Plugin metadata OK: version $($manifest.version), $($tools.Count) MCP tools, $($skillRefs.Count) explicit tool refs."
