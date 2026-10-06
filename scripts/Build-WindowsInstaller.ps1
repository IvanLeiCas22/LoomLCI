[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$PortableOutputRoot,
    [string]$Version,
    [string]$InnoCompiler,
    [string]$InstallRoot,
    [string]$LoomRoot,
    [string]$AppId,
    [switch]$SkipPortableTests
)

$ErrorActionPreference = 'Stop'

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
                Write-Warning "No se pudo limpiar staging temporal '$Path': $($_.Exception.Message)"
                return
            }

            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

function Get-Sha256WithRetry {
    param([Parameter(Mandatory)][string]$Path)

    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            return (Get-FileHash $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
        }
        catch {
            if ($attempt -eq 8) {
                throw
            }

            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\installer'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

$cleanupPortableRoot = $false
if ([string]::IsNullOrWhiteSpace($PortableOutputRoot)) {
    $PortableOutputRoot = Join-Path (
        [System.IO.Path]::GetTempPath()
    ) ("LoomLCI.Installer." + [Guid]::NewGuid().ToString('N'))
    $cleanupPortableRoot = $true
}
$PortableOutputRoot = [System.IO.Path]::GetFullPath($PortableOutputRoot)

if ([string]::IsNullOrWhiteSpace($Version)) {
    $sha = (& git -C $repoRoot rev-parse --short=12 HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        throw 'No se pudo resolver el commit Git para versionar el instalador.'
    }

    $Version = "0.1.0-dev-$sha"
}
if ($Version -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Versión inválida para el instalador: $Version"
}

$expectedInnoVersion = '7.1.0'
if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    if (-not [string]::IsNullOrWhiteSpace($env:LOOMLCI_ISCC)) {
        $InnoCompiler = $env:LOOMLCI_ISCC
    }
    else {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
        )
        $InnoCompiler = $candidates |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path $InnoCompiler)) {
    throw 'No se encontró Inno Setup 7.1.0. Instale JRSoftware.InnoSetup.7 o defina LOOMLCI_ISCC.'
}
$InnoCompiler = [System.IO.Path]::GetFullPath($InnoCompiler)

$actualInnoVersion = (& $InnoCompiler --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualInnoVersion -ne $expectedInnoVersion) {
    throw "Inno Setup inesperado. Esperado $expectedInnoVersion; encontrado '$actualInnoVersion'."
}

try {
    $portableArgs = @{
        OutputRoot = $PortableOutputRoot
        Version = $Version
    }
    if ($SkipPortableTests) {
        $portableArgs.SkipTests = $true
    }

    & (Join-Path $PSScriptRoot 'Build-PortablePackage.ps1') @portableArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Falló Build-PortablePackage.ps1.'
    }

    $packageDir = Join-Path $PortableOutputRoot "LoomLCI-$Version-win-x64"
    if (-not (Test-Path (Join-Path $packageDir 'LoomLCI.Launcher.exe')) -or
        -not (Test-Path (Join-Path $packageDir 'package.json')) -or
        -not (Test-Path (Join-Path $packageDir 'payload\host\LoomLCI.Host.exe'))) {
        throw "El portable no quedó completo: $packageDir"
    }

    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

    $installerScript = Join-Path $repoRoot 'installer\LoomLCI.iss'
    if (-not (Test-Path $installerScript)) {
        throw "Falta script de Inno Setup: $installerScript"
    }

    $isccArgs = @(
        "--output-dir=$OutputRoot",
        "--output-filename=LoomLCI-$Version-win-x64-setup",
        "--define=AppVersion=$Version",
        "--define=PackageRoot=$packageDir"
    )
    if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
        $isccArgs += "--define=InstallRoot=$([System.IO.Path]::GetFullPath($InstallRoot))"
    }
    if (-not [string]::IsNullOrWhiteSpace($LoomRoot)) {
        $isccArgs += "--define=LoomRoot=$([System.IO.Path]::GetFullPath($LoomRoot))"
    }
    if (-not [string]::IsNullOrWhiteSpace($AppId)) {
        $isccArgs += "--define=LoomAppId=$AppId"
    }
    $isccArgs += $installerScript

    & $InnoCompiler @isccArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Falló la compilación del instalador con Inno Setup.'
    }

    $installerPath = Join-Path $OutputRoot "LoomLCI-$Version-win-x64-setup.exe"
    if (-not (Test-Path $installerPath)) {
        throw "No se generó el instalador esperado: $installerPath"
    }

    $hash = Get-Sha256WithRetry -Path $installerPath
    Write-Host "Inno Setup: $actualInnoVersion"
    Write-Host "Installer:  $installerPath"
    Write-Host "SHA256:     $hash"
}
finally {
    if ($cleanupPortableRoot) {
        Remove-DirectoryBestEffort -Path $PortableOutputRoot
    }
}
