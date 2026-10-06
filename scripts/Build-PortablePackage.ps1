[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$Version,
    [long]$Sequence = 0,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\portable'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $sha = (& git -C $repoRoot rev-parse --short=12 HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        throw 'No se pudo resolver el commit Git para versionar el paquete.'
    }

    $Version = "0.1.0-dev-$sha"
}

if ($Version -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Versión inválida para nombre de directorio: $Version"
}
if ($Sequence -lt 0) {
    throw "Sequence inválido: $Sequence"
}

$packageDir = Join-Path $OutputRoot "LoomLCI-$Version-win-x64"
$zipPath = "$packageDir.zip"
$hostOut = Join-Path $packageDir 'payload\host'
$launcherPublish = Join-Path $packageDir '.launcher-publish'

if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

New-Item -ItemType Directory -Path $hostOut -Force | Out-Null
New-Item -ItemType Directory -Path $launcherPublish -Force | Out-Null

Write-Host "Publishing LoomLCI.Host $Version..."
$hostArgs = @(
    'publish',
    (Join-Path $repoRoot 'src\LoomLCI.Host\LoomLCI.Host.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $hostOut
)
& dotnet @hostArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Falló publish de LoomLCI.Host.'
}

$pdfWorkerAssembly = Join-Path $hostOut 'LoomLCI.PdfWorker.dll'
$pdfPigAssembly = Join-Path $hostOut 'UglyToad.PdfPig.dll'
$pdfiumNative = Join-Path $hostOut 'pdfium.dll'
$stbImageWriteAssembly = Join-Path $hostOut 'StbImageWriteSharp.dll'
if (-not (Test-Path $pdfWorkerAssembly) -or
    -not (Test-Path $pdfPigAssembly) -or
    -not (Test-Path $pdfiumNative) -or
    -not (Test-Path $stbImageWriteAssembly)) {
    throw 'El Host publicado no contiene los assets PDF requeridos por G1.2/G1.3 (PdfWorker, PdfPig, pdfium.dll, StbImageWriteSharp).'
}

Write-Host 'Publishing single-file LoomLCI.Launcher...'
$launcherArgs = @(
    'publish',
    (Join-Path $repoRoot 'src\LoomLCI.Launcher\LoomLCI.Launcher.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $launcherPublish
)
& dotnet @launcherArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Falló publish de LoomLCI.Launcher.'
}

$launcherExe = Join-Path $launcherPublish 'LoomLCI.Launcher.exe'
if (-not (Test-Path $launcherExe)) {
    throw "No se generó $launcherExe"
}

Copy-Item $launcherExe (Join-Path $packageDir 'LoomLCI.Launcher.exe') -Force
Remove-Item $launcherPublish -Recurse -Force

$thirdPartyNotices = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt'
$pdfiumLicense = Join-Path $repoRoot 'licenses\PDFium-LICENSE.txt'
$apacheLicense = Join-Path $repoRoot 'licenses\Apache-2.0.txt'
if (-not (Test-Path $thirdPartyNotices) -or
    -not (Test-Path $pdfiumLicense) -or
    -not (Test-Path $apacheLicense)) {
    throw 'Faltan THIRD-PARTY-NOTICES.txt y/o licencias requeridas por G1.3.'
}
Copy-Item $thirdPartyNotices (Join-Path $packageDir 'THIRD-PARTY-NOTICES.txt') -Force
$licensesOut = Join-Path $packageDir 'licenses'
New-Item -ItemType Directory -Path $licensesOut -Force | Out-Null
Copy-Item $pdfiumLicense (Join-Path $licensesOut 'PDFium-LICENSE.txt') -Force
Copy-Item $apacheLicense (Join-Path $licensesOut 'Apache-2.0.txt') -Force

$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    sequence = $Sequence
    updateProtocol = 1
    platform = 'win-x64'
    hostRelativePath = 'payload/host'
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $packageDir 'package.json') -Encoding UTF8

$readmeTemplatePath = Join-Path $PSScriptRoot 'PortableReadme.template.txt'
if (-not (Test-Path $readmeTemplatePath)) {
    throw "Falta template README portable: $readmeTemplatePath"
}
$readme = [System.IO.File]::ReadAllText(
    $readmeTemplatePath,
    [System.Text.Encoding]::UTF8).Replace('{{VERSION}}', $Version)
[System.IO.File]::WriteAllText(
    (Join-Path $packageDir 'README.txt'),
    $readme,
    [System.Text.Encoding]::UTF8)

if (-not $SkipTests) {
    Write-Host 'Running Launcher tests...'
    $launcherTestArgs = @(
        'test',
        (Join-Path $repoRoot 'tests\LoomLCI.Launcher.Tests\LoomLCI.Launcher.Tests.csproj'),
        '-c', 'Release'
    )
    & dotnet @launcherTestArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Fallaron los tests de LoomLCI.Launcher.'
    }

    Write-Host 'Running MCP integration tests against the published Host...'
    $previousHost = $env:LOOMLCI_TEST_HOST_DLL
    try {
        $env:LOOMLCI_TEST_HOST_DLL = Join-Path $hostOut 'LoomLCI.Host.dll'
        $integrationArgs = @(
            'test',
            (Join-Path $repoRoot 'tests\LoomLCI.IntegrationTests\LoomLCI.IntegrationTests.csproj'),
            '-c', 'Release'
        )
        & dotnet @integrationArgs
        if ($LASTEXITCODE -ne 0) {
            throw 'Fallaron los integration tests contra el Host publicado.'
        }
    }
    finally {
        $env:LOOMLCI_TEST_HOST_DLL = $previousHost
    }
}

Write-Host 'Creating portable ZIP...'
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Package: $packageDir"
Write-Host "ZIP:     $zipPath"
Write-Host "SHA256:  $zipHash"
