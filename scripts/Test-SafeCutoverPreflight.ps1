#Requires -Version 5.1
# Read-only and fail-closed tests; no production directories or runtime processes.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cutover = Join-Path $PSScriptRoot 'Invoke-SafeCutover.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('LoomLCI.RB04.' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'test-install'
$data = Join-Path $root 'test-data'
$backup = Join-Path $root 'test-backup'
$versions = Join-Path $install 'versions\v1'
$configDir = Join-Path $data 'config'
$keyDir = Join-Path $data 'secrets'
$installer = Join-Path $root 'fake-setup.exe'
$launcher = Join-Path $install 'LoomLCI.Launcher.exe'
$configPath = Join-Path $configDir 'machine.json'
New-Item -ItemType Directory -Path $versions, $configDir, $keyDir -Force | Out-Null
try {
    [IO.File]::WriteAllText($launcher, 'dummy launcher; never executed')
    [IO.File]::WriteAllText((Join-Path $versions 'LoomLCI.Host.exe'), 'dummy host')
    [IO.File]::WriteAllText($installer, 'dummy installer; never executed')
    [IO.File]::WriteAllText((Join-Path $keyDir 'runtime-api-key.txt'), 'dummy-local-test-key')
    $config = @{
        activeVersion = 'v1'
        activeSequence = 1
        previousVersion = $null
        previousSequence = 0
        tunnelId = 'tunnel_test'
        alias = 'loomlci-installed'
    } | ConvertTo-Json
    [IO.File]::WriteAllText($configPath, $config)
    $expectedHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $metadataPath = [IO.Path]::ChangeExtension($installer, 'deployment.json')
    $metadata = [ordered]@{
        schemaVersion = 1
        installerFormat = 'inno-setup7'
        version = 'v2'
        sequence = 2
        installRoot = $install
        dataRoot = $data
        installerSha256 = $expectedHash
    }
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 4))
    $initialConfigHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    $baseArgs = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $cutover,
        '-InstallerPath', $installer,
        '-InstallerSha256', $expectedHash,
        '-TargetVersion', 'v2', '-TargetSequence', '2',
        '-ExpectedCurrentVersion', 'v1', '-ExpectedCurrentSequence', '1',
        '-InstallRoot', $install, '-DataRoot', $data, '-BackupRoot', $backup)

    function Invoke-TestCase([string]$name, [string[]]$more, [int]$expectedCode, [string]$expectedText) {
        $ErrorActionPreference = 'Continue'
        $lines = & powershell.exe @baseArgs @more 2>&1 | Out-String
        $code = $LASTEXITCODE
        if (($expectedCode -eq 0 -and $code -ne 0) -or
            ($expectedCode -ne 0 -and $code -eq 0) -or
            ($lines -notmatch [regex]::Escape($expectedText))) {
            throw "${name}: expected exit $expectedCode and text '$expectedText'; actual $code. Output: $lines"
        }
        $afterConfigHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
        if ($afterConfigHash -ne $initialConfigHash) {
            throw "${name}: modified the isolated machine.json"
        }
        if (Test-Path -LiteralPath $backup) {
            throw "${name}: created a backup unexpectedly"
        }
        Write-Host "$name PASS (exit $code)"
    }

    Invoke-TestCase 'valid_preflight' @('-Mode','Preflight') 0 'Preflight OK'

    [IO.File]::WriteAllText($installer, 'tampered test executable')
    Invoke-TestCase 'wrong_installer_hash' @('-Mode','Preflight') 1 'installer SHA-256 mismatch'
    [IO.File]::WriteAllText($installer, 'dummy installer; never executed')

    $index = [Array]::IndexOf($baseArgs, '-ExpectedCurrentVersion')
    $baseArgs[$index + 1] = 'v0'
    Invoke-TestCase 'stale_current_version' @('-Mode','Preflight') 1 'Active version changed since approval'
    $baseArgs[$index + 1] = 'v1'

    $metadata.installRoot = Join-Path $root 'incorrect-install'
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 4))
    Invoke-TestCase 'wrong_installer_target' @('-Mode','Preflight') 1 'Installer was built for a different InstallRoot'
    $metadata.installRoot = $install
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 4))

    $metadata.installerSha256 = ('0' * 64)
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 4))
    Invoke-TestCase 'wrong_metadata_hash' @('-Mode','Preflight') 1 'Installer metadata SHA-256 mismatch'
    $metadata.installerSha256 = $expectedHash
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 4))

    Invoke-TestCase 'execute_without_confirmation' @('-Mode','Execute') 1 'Execute requires -ConfirmExternalSupervisor'

    $journalDir = Join-Path $data 'update'
    New-Item -ItemType Directory -Path $journalDir -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $journalDir 'journal.json'), 'test pending recovery')
    Invoke-TestCase 'pending_journal' @('-Mode','Preflight') 1 'A recovery journal exists'

    Write-Host 'RB04_ISOLATED_PREFLIGHT_OK (7/7)'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
