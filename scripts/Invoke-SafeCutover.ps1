#Requires -Version 5.1
<#
.SYNOPSIS
  Preflight or perform a supervised LoomLCI installer cutover.
.DESCRIPTION
  Execute ONLY from a launcher started outside the LoomLCI runtime being stopped
  (e.g. IvanSpace started by Windows Task Scheduler).
  Preflight is read-only. Execute is opt-in and records a durable recovery folder.
  This does not install a Windows service or change scheduled tasks.
#>
[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Execute')]
    [string]$Mode = 'Preflight',
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$InstallerSha256,
    [Parameter(Mandatory)][string]$TargetVersion,
    [Parameter(Mandatory)][long]$TargetSequence,
    [Parameter(Mandatory)][string]$ExpectedCurrentVersion,
    [Parameter(Mandatory)][long]$ExpectedCurrentSequence,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\LoomLCI'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'LoomLCI\deployment'),
    [string]$BackupRoot = (Join-Path $env:LOCALAPPDATA 'LoomLCI-CutoverBackups'),
    [string]$ExpectedLauncherSha256,
    [string]$ExpectedHostSha256,
    [switch]$ConfirmExternalSupervisor
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Hash([string]$path, [string]$expected, [string]$label) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing $label at $path"
    Assert-True ($expected -match '^[0-9a-fA-F]{64}$') "Invalid expected SHA-256 for $label"
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Assert-True ([string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) "$label SHA-256 mismatch"
}

function Assert-SeparatePath([string]$root, [string]$other) {
    $canonicalRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $canonicalOther = [IO.Path]::GetFullPath($other).TrimEnd('\') + '\'
    Assert-True (-not $canonicalRoot.StartsWith($canonicalOther, [StringComparison]::OrdinalIgnoreCase) -and
                 -not $canonicalOther.StartsWith($canonicalRoot, [StringComparison]::OrdinalIgnoreCase)) "BackupRoot must be separate from install/data root"
}

function Assert-ExternalAncestry {
    $pidToCheck = $PID
    $seen = @{}
    for ($depth = 0; $depth -lt 24; $depth++) {
        if ($seen.ContainsKey("$pidToCheck")) { break }
        $seen["$pidToCheck"] = $true
        $process = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $pidToCheck) -ErrorAction Stop
        if ($null -eq $process) { break }
        Assert-True ($process.Name -ine 'LoomLCI.Host.exe') "Cannot cut over from a LoomLCI.Host.exe process tree"
        $pidToCheck = [int]$process.ParentProcessId
        if ($pidToCheck -le 4) { break }
    }
    # This ancestry check is necessary but not sufficient: nested Job Objects
    # must also be validated in an independent rehearsal of the supervisor.
}

function Invoke-Launcher([string[]]$launcherArguments) {
    $output = & $script:launcher @launcherArguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host ("Launcher " + ($launcherArguments -join ' ') + ": exit " + $code)
    if ($output.Trim()) { Write-Host $output.Trim() }
    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

function Load-Config {
    $path = Join-Path $script:DataRoot 'config\machine.json'
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing machine.json"
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Save-Event([string]$text) {
    if ($script:report) {
        Add-Content -LiteralPath $script:report -Encoding UTF8 -Value (
            (Get-Date).ToUniversalTime().ToString('o') + ' ' + $text)
    }
    Write-Host $text
}

function Check-Status([string]$description) {
    $status = Invoke-Launcher @('status')
    $versionPattern = '(?m)^LoomLCI version:\s*' +
        [regex]::Escape($script:ExpectedReadyVersion) + '\s*$'
    $tunnelPattern = '(?m)^tunnel:\s*' +
        [regex]::Escape($script:ExpectedTunnelId) + '\s*$'
    Assert-True ($status.ExitCode -eq 0 -and
                 $status.Output -match '(?m)^healthy:\s*True\s*$' -and
                 $status.Output -match '(?m)^ready:\s*True\s*$' -and
                 $status.Output -match '(?m)^process_running:\s*True\s*$' -and
                 $status.Output -match $versionPattern -and
                 $status.Output -match $tunnelPattern) "$description has unexpected identity or is not healthy/ready"
}

function Make-Backup([object]$originalConfig) {
    $id = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N')
    $directory = Join-Path $script:BackupRoot $id
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath $script:launcher -Destination (Join-Path $directory 'LoomLCI.Launcher.exe') -ErrorAction Stop
    Copy-Item -LiteralPath (Join-Path $script:DataRoot 'config\machine.json') -Destination (Join-Path $directory 'machine.json') -ErrorAction Stop
    $record = [ordered]@{
        createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        originalVersion = $originalConfig.activeVersion
        originalSequence = [long]$originalConfig.activeSequence
        targetVersion = $TargetVersion
        targetSequence = $TargetSequence
        installerSha256 = $InstallerSha256
        launcherSha256 = (Get-FileHash -LiteralPath $script:launcher -Algorithm SHA256).Hash
        note = 'Do not restore machine.json blindly after a partial setup; investigate installed Host and journal first.'
    }
    [IO.File]::WriteAllText((Join-Path $directory 'metadata.json'),
        ($record | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    $script:report = Join-Path $directory 'cutover.log'
    Save-Event "Backup: $directory"
    return $directory
}

$script:report = $null
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$BackupRoot = [IO.Path]::GetFullPath($BackupRoot)
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$script:launcher = Join-Path $InstallRoot 'LoomLCI.Launcher.exe'
Assert-True ($TargetVersion -match '^[A-Za-z0-9._-]+$' -and $TargetVersion -notin @('.','..')) 'Invalid TargetVersion'
Assert-True ($ExpectedCurrentVersion -match '^[A-Za-z0-9._-]+$' -and $ExpectedCurrentVersion -notin @('.','..')) 'Invalid ExpectedCurrentVersion'
Assert-True ($TargetSequence -gt $ExpectedCurrentSequence) 'TargetSequence must increase'
Assert-SeparatePath $BackupRoot $InstallRoot
Assert-SeparatePath $BackupRoot $DataRoot
Assert-Hash $InstallerPath $InstallerSha256 'installer'
$metadataPath = [IO.Path]::ChangeExtension($InstallerPath, 'deployment.json')
Assert-True (Test-Path -LiteralPath $metadataPath -PathType Leaf) 'Missing installer deployment metadata'
$metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-True ([int]$metadata.schemaVersion -eq 1 -and $metadata.installerFormat -eq 'inno-setup7') 'Invalid installer deployment metadata'
Assert-True ($metadata.version -ceq $TargetVersion -and
             [long]$metadata.sequence -eq $TargetSequence) 'Installer metadata has unexpected version/sequence'
Assert-True ([string]::Equals($metadata.installerSha256, $InstallerSha256,
             [StringComparison]::OrdinalIgnoreCase)) 'Installer metadata SHA-256 mismatch'
$metadataInstall = [IO.Path]::GetFullPath(([string]$metadata.installRoot).Replace('{localappdata}', $env:LOCALAPPDATA))
$metadataData = [IO.Path]::GetFullPath(([string]$metadata.dataRoot).Replace('{localappdata}', $env:LOCALAPPDATA))
Assert-True ([string]::Equals($metadataInstall, $InstallRoot, [StringComparison]::OrdinalIgnoreCase)) 'Installer was built for a different InstallRoot'
Assert-True ([string]::Equals($metadataData, $DataRoot, [StringComparison]::OrdinalIgnoreCase)) 'Installer was built for a different DataRoot'
Assert-True (Test-Path -LiteralPath $script:launcher -PathType Leaf) 'Missing installed Launcher'
$config = Load-Config
Assert-True ($config.activeVersion -ceq $ExpectedCurrentVersion) 'Active version changed since approval'
Assert-True ([long]$config.activeSequence -eq $ExpectedCurrentSequence) 'Active sequence changed since approval'
Assert-True ($config.tunnelId -match '^tunnel_.+') 'Invalid configured tunnel ID'
Assert-True ($config.alias -eq 'loomlci-installed') 'This cutover requires the standard installed alias'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $DataRoot 'update\journal.json'))) 'A recovery journal exists; recover before cutover'
Assert-True (Test-Path -LiteralPath (Join-Path $InstallRoot ("versions\" + $config.activeVersion + '\LoomLCI.Host.exe')) -PathType Leaf) 'Active Host is missing'
$keyPath = Join-Path $DataRoot 'secrets\runtime-api-key.txt'
Assert-True (Test-Path -LiteralPath $keyPath -PathType Leaf) 'Runtime API key file is missing'
if ($ExpectedLauncherSha256) {
    Assert-True ($ExpectedLauncherSha256 -match '^[0-9a-fA-F]{64}$') 'Invalid expected Launcher SHA-256'
}
if ($ExpectedHostSha256) {
    Assert-True ($ExpectedHostSha256 -match '^[0-9a-fA-F]{64}$') 'Invalid expected Host SHA-256'
}
Write-Output ("Preflight OK: " + $config.activeVersion + '/' + $config.activeSequence +
    ' -> ' + $TargetVersion + '/' + $TargetSequence)
Write-Output ("Paths: install=$InstallRoot ; data=$DataRoot ; backup=$BackupRoot")
if ($Mode -eq 'Preflight') { return }

Assert-True $ConfirmExternalSupervisor.IsPresent 'Execute requires -ConfirmExternalSupervisor'
Assert-True (-not [string]::IsNullOrWhiteSpace($ExpectedLauncherSha256)) 'Execute requires ExpectedLauncherSha256'
Assert-True (-not [string]::IsNullOrWhiteSpace($ExpectedHostSha256)) 'Execute requires ExpectedHostSha256'
Assert-ExternalAncestry
$script:ExpectedReadyVersion = $ExpectedCurrentVersion
$script:ExpectedTunnelId = [string]$config.tunnelId
Check-Status 'Original runtime'
$backup = Make-Backup $config

try {
    Save-Event 'Stopping old runtime'
    $stopped = Invoke-Launcher @('stop')
    Assert-True ($stopped.ExitCode -eq 0) 'Failed to stop old runtime'

    Save-Event 'Executing pinned Inno Setup installer'
    # The package must be compiled for the SAME InstallRoot / DataRoot.
    # NOSHORTCUTS preserves the user's pre-existing desktop shortcuts.
    $args = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        ('/TUNNELID=' + $config.tunnelId),
        ('/RUNTIMEKEYFILE="' + $keyPath + '"'), '/NOSHORTCUTS=1')
    $installerProcess = Start-Process -FilePath $InstallerPath -ArgumentList $args -Wait -PassThru -ErrorAction Stop
    Save-Event ("Installer exit: " + $installerProcess.ExitCode)
    Assert-True ($installerProcess.ExitCode -eq 0) 'Installer failed; preserved backup'
    $newConfig = Load-Config
    Assert-True ($newConfig.activeVersion -ceq $TargetVersion -and
                 [long]$newConfig.activeSequence -eq $TargetSequence) 'Installer installed unexpected version/sequence'
    Assert-Hash $script:launcher $ExpectedLauncherSha256 'installed Launcher'
    $hostExe = Join-Path $InstallRoot ("versions\" + $TargetVersion + '\LoomLCI.Host.exe')
    Assert-Hash $hostExe $ExpectedHostSha256 'installed Host'
    Assert-True ($newConfig.previousVersion -ceq $config.activeVersion -and
                 [long]$newConfig.previousSequence -eq [long]$config.activeSequence) 'Previous version was not preserved'

    Save-Event 'Starting new runtime'
    $started = Invoke-Launcher @('start')
    Assert-True ($started.ExitCode -eq 0) 'Failed to start new runtime'
    $script:ExpectedReadyVersion = $TargetVersion
    Check-Status 'New runtime'
    Save-Event 'CUTOVER_OK'
}
catch {
    Save-Event ('CUTOVER_FAILED: ' + $_.Exception.Message)
    Save-Event 'Backup and journal remain available. No blind file restoration performed.'
    # Try to revive the original runtime only if config still references it.
    try {
        $current = Load-Config
        if ($current.activeVersion -ceq $config.activeVersion -and
            [long]$current.activeSequence -eq [long]$config.activeSequence) {
            Save-Event 'Attempting to restart unchanged original runtime'
            $null = Invoke-Launcher @('start')
        }
        else {
            Save-Event 'Target config has changed: inspect the journal and use supervised Launcher rollback.'
        }
    }
    catch {
        Save-Event ('Automatic restart unavailable: ' + $_.Exception.Message)
    }
    throw
}
