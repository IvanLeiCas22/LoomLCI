[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [long]$Sequence,

    [string]$OutputRoot,
    [string]$SigningKeyPath,
    [string]$Repository = 'IvanLeiCas22/LoomLCI',
    [string]$Tag,
    [string]$PackageBaseUrl,
    [switch]$SkipTests
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
                Write-Warning "No se pudo limpiar staging '$Path': $($_.Exception.Message)"
                return
            }

            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Assert-UserOnlySigningKey {
    param([Parameter(Mandatory)][string]$Path)

    $acl = Get-Acl $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "La clave de firma debe tener herencia ACL deshabilitada: $Path"
    }

    $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($rule in $acl.Access) {
        $ruleSid = $rule.IdentityReference.Translate(
            [System.Security.Principal.SecurityIdentifier])
        if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
            $ruleSid.Value -ne $user.Value) {
            throw "La clave de firma permite acceso a otra identidad: $($rule.IdentityReference)"
        }
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\release'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($SigningKeyPath)) {
    $SigningKeyPath = Join-Path $env:LOCALAPPDATA 'LoomLCI-ReleaseSigning\update-signing-private.cng'
}
$SigningKeyPath = [System.IO.Path]::GetFullPath($SigningKeyPath)

if (-not (Test-Path $SigningKeyPath)) {
    throw "Falta la clave privada de firma: $SigningKeyPath"
}
Assert-UserOnlySigningKey -Path $SigningKeyPath

if ($Version -notmatch '^[A-Za-z0-9._-]+$' -or $Version -eq '.' -or $Version -eq '..') {
    throw "Versión inválida: $Version"
}
if ($Sequence -le 0) {
    throw "Sequence inválido: $Sequence"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repositorio inválido: $Repository"
}
if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "v$Version"
}
if ($Tag -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Tag inválido: $Tag"
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'LoomLCI.Release.' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stagingRoot 'package'
$hostOut = Join-Path $packageRoot 'payload\host'
New-Item -ItemType Directory -Path $hostOut -Force | Out-Null

try {
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

    $required = @(
        'LoomLCI.Host.exe',
        'LoomLCI.Host.dll',
        'LoomLCI.PdfWorker.dll',
        'UglyToad.PdfPig.dll',
        'pdfium.dll',
        'StbImageWriteSharp.dll'
    )
    foreach ($name in $required) {
        if (-not (Test-Path (Join-Path $hostOut $name))) {
            throw "El Host publicado no contiene $name."
        }
    }

    $packageManifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        sequence = $Sequence
        updateProtocol = 1
        platform = 'win-x64'
        hostRelativePath = 'payload/host'
    }
    $packageJson = $packageManifest | ConvertTo-Json
    Write-Utf8NoBom -Path (Join-Path $packageRoot 'package.json') -Text $packageJson

    $thirdPartyNotices = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt'
    $pdfiumLicense = Join-Path $repoRoot 'licenses\PDFium-LICENSE.txt'
    $apacheLicense = Join-Path $repoRoot 'licenses\Apache-2.0.txt'
    if (-not (Test-Path $thirdPartyNotices) -or
        -not (Test-Path $pdfiumLicense) -or
        -not (Test-Path $apacheLicense)) {
        throw 'Faltan notices/licencias requeridos.'
    }

    Copy-Item $thirdPartyNotices (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.txt') -Force
    $licensesOut = Join-Path $packageRoot 'licenses'
    New-Item -ItemType Directory -Path $licensesOut -Force | Out-Null
    Copy-Item $pdfiumLicense (Join-Path $licensesOut 'PDFium-LICENSE.txt') -Force
    Copy-Item $apacheLicense (Join-Path $licensesOut 'Apache-2.0.txt') -Force

    if (-not $SkipTests) {
        Write-Host 'Running Launcher tests...'
        & dotnet test (
            Join-Path $repoRoot 'tests\LoomLCI.Launcher.Tests\LoomLCI.Launcher.Tests.csproj'
        ) -c Release
        if ($LASTEXITCODE -ne 0) {
            throw 'Fallaron los tests de LoomLCI.Launcher.'
        }

        Write-Host 'Running IntegrationTests against the update Host...'
        $previousHost = $env:LOOMLCI_TEST_HOST_DLL
        try {
            $env:LOOMLCI_TEST_HOST_DLL = Join-Path $hostOut 'LoomLCI.Host.dll'
            & dotnet test (
                Join-Path $repoRoot 'tests\LoomLCI.IntegrationTests\LoomLCI.IntegrationTests.csproj'
            ) -c Release
            if ($LASTEXITCODE -ne 0) {
                throw 'Fallaron los IntegrationTests contra el Host de update.'
            }
        }
        finally {
            $env:LOOMLCI_TEST_HOST_DLL = $previousHost
        }
    }

    $packageName = "LoomLCI-$Version-win-x64-update.zip"
    $packagePath = Join-Path $OutputRoot $packageName
    if (Test-Path $packagePath) {
        Remove-Item $packagePath -Force
    }

    Write-Host 'Creating update ZIP...'
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $packagePath -CompressionLevel Optimal

    $packageFile = Get-Item $packagePath
    $packageHash = (Get-FileHash $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($PackageBaseUrl)) {
        $packageUrl = "https://github.com/$Repository/releases/download/$Tag/$packageName"
    }
    else {
        $baseUri = [Uri]$PackageBaseUrl
        if (-not $baseUri.IsAbsoluteUri -or
            -not ($baseUri.Scheme -eq 'https' -or
                  ($baseUri.Scheme -eq 'http' -and $baseUri.IsLoopback))) {
            throw 'PackageBaseUrl debe usar HTTPS; HTTP sólo se admite en loopback.'
        }
        $packageUrl = $PackageBaseUrl.TrimEnd('/') + '/' + $packageName
    }

    $releaseManifest = [ordered]@{
        schemaVersion = 1
        channel = 'stable'
        sequence = $Sequence
        version = $Version
        platform = 'win-x64'
        minUpdateProtocol = 1
        packageUrl = $packageUrl
        packageSizeBytes = [int64]$packageFile.Length
        packageSha256 = $packageHash
    }

    $manifestPath = Join-Path $OutputRoot 'loomlci-update.json'
    $manifestJson = $releaseManifest | ConvertTo-Json
    Write-Utf8NoBom -Path $manifestPath -Text $manifestJson
    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)

    $privateBlob = [System.IO.File]::ReadAllBytes($SigningKeyPath)
    $key = [System.Security.Cryptography.CngKey]::Import(
        $privateBlob,
        [System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    try {
        $ecdsa = [System.Security.Cryptography.ECDsaCng]::new($key)
        try {
            $signature = $ecdsa.SignData(
                $manifestBytes,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        }
        finally {
            $ecdsa.Dispose()
        }
    }
    finally {
        $key.Dispose()
    }

    $signaturePath = Join-Path $OutputRoot 'loomlci-update.sig'
    Write-Utf8NoBom -Path $signaturePath -Text ([Convert]::ToBase64String($signature))

    Write-Host "Update ZIP: $packagePath"
    Write-Host "Size:       $($packageFile.Length)"
    Write-Host "SHA256:     $packageHash"
    Write-Host "Manifest:   $manifestPath"
    Write-Host "Signature:  $signaturePath"
    Write-Host "Tag:        $Tag"
}
finally {
    Remove-DirectoryBestEffort -Path $stagingRoot
}
