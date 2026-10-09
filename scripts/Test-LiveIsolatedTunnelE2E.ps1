#Requires -Version 5.1
<#
  RB-04: Genuine isolated live-tunnel E2E with the previously approved HTTP
  test tunnel. External supervisor REQUIRED: execute using IvanSpace, not
  LoomLCI.Host itself. No productive runtime is stopped or modified.
  Copies only the official pinned tunnel-client executable as a test seed.
  Uses the existing HTTP-test key *by file path*; never prints its content.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) ('LoomLCI.RB04.Live.' + $id.Substring(0,12))
$install = Join-Path $root 'install'
$loomRoot = Join-Path $root 'loom'
$data = Join-Path $loomRoot 'deployment'
$backup = Join-Path $root 'backups'
$releases = Join-Path $root 'releases'
$portable = Join-Path $root 'portable'
$appId = 'LoomLCI.RB04.Live.' + $id
$v1 = 'r4-live-a-' + $id.Substring(0,12)
$v2 = 'r4-live-b-' + $id.Substring(0,12)
$tunnelId = 'tunnel_6ac73f6a8cfc8191b551818597fc5413'
$keyPath = Join-Path $env:LOCALAPPDATA 'LoomLCI\http-test\secrets\runtime-api-key.txt'
$launcher = Join-Path $install 'LoomLCI.Launcher.exe'
$productLauncher = Join-Path $env:LOCALAPPDATA 'Programs\LoomLCI\LoomLCI.Launcher.exe'
$productClient = Join-Path $env:LOCALAPPDATA 'Programs\LoomLCI\tools\tunnel-client.exe'
$log = Join-Path $root 'e2e.log'
$started = $false
$installed = $false
$passed = $false
$baseline = @{}
$env:LOOMLCI_INSTALL_ROOT = $install
$env:LOOMLCI_DATA_ROOT = $data

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Log([string]$message) {
    $line = (Get-Date).ToUniversalTime().ToString('o') + ' ' + $message
    [IO.File]::AppendAllText($script:log, $line + [Environment]::NewLine)
    Write-Host $message
}
function Inv([string[]]$launcherArgs) {
    $output = & $script:launcher @launcherArgs 2>&1 | Out-String
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
        # Do not leak potentially sensitive stdout/stderr into logs.
        throw ('Isolated Launcher ' + $launcherArgs[0] + ' exit ' + $exit)
    }
    return $output
}
function Check([string]$version) {
    $c = Get-Content -LiteralPath (Join-Path $script:data 'config\machine.json') -Raw | ConvertFrom-Json
    Assert ($c.activeVersion -ceq $version) 'Wrong active version'
    Assert ($c.tunnelId -ceq $script:tunnelId) 'Wrong tunnel'
    Assert ($c.alias -ceq 'loomlci-installed') 'Wrong runtime alias'
    $out = Inv @('status')
    Assert ($out -match '(?m)^healthy:\s*True\s*$') 'Not healthy'
    Assert ($out -match '(?m)^ready:\s*True\s*$') 'Not ready'
    Assert ($out -match '(?m)^process_running:\s*True\s*$') 'Not running'
    Assert ($out -match ('(?m)^LoomLCI version:\s*'+[regex]::Escape($version)+'\s*$')) 'Wrong installed version'
    Assert ($out -match ('(?m)^tunnel:\s*'+[regex]::Escape($script:tunnelId)+'\s*$')) 'Wrong reported tunnel'
    Log ('HEALTH_OK '+$version)
}
function Package([string]$version,[long]$seq) {
    $out = Join-Path $script:releases $version
    $por = Join-Path $script:portable $version
    $buildArgs = @{
        OutputRoot = $out
        PortableOutputRoot = $por
        Version = $version
        Sequence = $seq
        InstallRoot = $script:install
        LoomRoot = $script:loomRoot
        AppId = $script:appId
        SkipPortableTests = $true
    }
    $buildLog = Join-Path $script:root ('build-'+$version+'.log')
    & (Join-Path $script:repo 'scripts\Build-WindowsInstaller.ps1') @buildArgs *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        $tail = Get-Content -LiteralPath $buildLog -Tail 20 | Out-String
        Write-Host $tail
        throw ('Build failed: '+$version)
    }
    $exe = Join-Path $out ('LoomLCI-'+$version+'-win-x64-setup.exe')
    $meta = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($exe,'deployment.json')) -Raw | ConvertFrom-Json
    $sha = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    Assert ($sha -ieq $meta.installerSha256) 'Setup SHA mismatch'
    Assert ($meta.installRoot -ieq $script:install) 'Setup install root mismatch'
    Assert ($meta.dataRoot -ieq $script:data) 'Setup data root mismatch'
    $launcherPath = Join-Path $por ('LoomLCI-'+$version+'-win-x64\LoomLCI.Launcher.exe')
    $hostPath = Join-Path $por ('LoomLCI-'+$version+'-win-x64\payload\host\LoomLCI.Host.exe')
    $obj = [pscustomobject]@{
        Setup = $exe
        Hash = $sha
        LauncherHash = (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash
        HostHash = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash
    }
    Log ('BUILD_OK '+$version)
    return $obj
}
function Install([object]$pkg) {
    $args = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        ('/TUNNELID=' + $script:tunnelId),
        ('/RUNTIMEKEYFILE="' + $script:keyPath + '"'),
        '/NOSHORTCUTS=1')
    $r = Start-Process -FilePath $pkg.Setup -ArgumentList $args -PassThru -Wait -ErrorAction Stop
    Assert ($r.ExitCode -eq 0) ('Real installer exit '+$r.ExitCode)
    $script:installed = $true
    Log 'GENUINE_SETUP_OK'
}
function ProductStatus {
    $savedInstall = $env:LOOMLCI_INSTALL_ROOT
    $savedData = $env:LOOMLCI_DATA_ROOT
    try {
        Remove-Item Env:\LOOMLCI_INSTALL_ROOT -ErrorAction SilentlyContinue
        Remove-Item Env:\LOOMLCI_DATA_ROOT -ErrorAction SilentlyContinue
        $out = & $script:productLauncher status 2>&1 | Out-String
        Assert ($LASTEXITCODE -eq 0) 'Productive Launcher status returned nonzero'
        return $out
    }
    finally {
        $env:LOOMLCI_INSTALL_ROOT = $savedInstall
        $env:LOOMLCI_DATA_ROOT = $savedData
    }
}
function DesktopBaseline {
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    foreach($name in @('LoomLCI.lnk','Detener LoomLCI.lnk')) {
        $p = Join-Path $desktop $name
        $script:baseline[$p] = if(Test-Path -LiteralPath $p) {
            (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
        } else { $null }
    }
}
function DesktopUnchanged {
    foreach($p in $script:baseline.Keys) {
        $original = $script:baseline[$p]
        $present = Test-Path -LiteralPath $p
        if ($null -eq $original) { Assert (-not $present) 'Desktop shortcut unexpectedly created' }
        else {
            Assert $present 'Desktop shortcut deleted'
            Assert ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ieq $original) 'Desktop shortcut unexpectedly replaced'
        }
    }
}

New-Item -ItemType Directory -Path $root, (Join-Path $install 'tools') -Force | Out-Null
try {
    Assert (Test-Path -LiteralPath $keyPath -PathType Leaf) 'HTTP test key missing'
    Assert ((Get-Item -LiteralPath $keyPath).Length -gt 0) 'HTTP test key empty'
    $keyAcl = Get-Acl -LiteralPath $keyPath
    Assert $keyAcl.AreAccessRulesProtected 'HTTP test key ACL must be inheritance-protected'
    $sha = (Get-FileHash -LiteralPath $productClient -Algorithm SHA256).Hash
    Assert ($sha -ieq 'fcc85a69ec0ad82518e4f8964f60c45e31787957782a0fc9c1b0c44e82d61b9b') 'Incorrect pinned tunnel client'
    DesktopBaseline
    $statusBefore = ProductStatus
    Assert ($statusBefore -match 'healthy:\s*True' -and $statusBefore -match 'ready:\s*True') 'Productive status invalid at baseline'
    Assert ($statusBefore -notmatch [regex]::Escape($tunnelId)) 'Test tunnel is the productive tunnel'
    Copy-Item -LiteralPath $productClient -Destination (Join-Path $install 'tools\tunnel-client.exe')
    Log 'PREFLIGHT_OK: separate test tunnel, pinned client and protected key'

    # Remote test tunnel may already have an active HTTP runtime. Do not steal it.
    $httpRuntime = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -notin @('powershell.exe','pwsh.exe','cmd.exe') -and
        ($_.CommandLine -like '*a32-http-test*' -or $_.CommandLine -like '*LoomLCI.HttpPoc*')
    })
    Assert ($httpRuntime.Count -eq 0) 'Old HTTP test runtime is active; aborting'
    $package1 = Package $v1 701
    Install $package1
    $out = Inv @('start')
    $started = $true
    Check $v1
    Log 'INSTALL_AND_CONNECT_OK'

    $package2 = Package $v2 702
    $cutoverScript = Join-Path $repo 'scripts\Invoke-SafeCutover.ps1'
    $opts = @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
        '-File', $cutoverScript,
        '-InstallerPath',$package2.Setup,
        '-InstallerSha256',$package2.Hash,
        '-TargetVersion',$v2,
        '-TargetSequence','702',
        '-ExpectedCurrentVersion',$v1,
        '-ExpectedCurrentSequence','701',
        '-InstallRoot',$install,
        '-DataRoot',$data,
        '-BackupRoot',$backup,
        '-ExpectedLauncherSha256',$package2.LauncherHash,
        '-ExpectedHostSha256',$package2.HostHash
    )
    $preflight = & powershell.exe @opts '-Mode' 'Preflight' 2>&1 | Out-String
    Assert ($LASTEXITCODE -eq 0 -and $preflight -match 'Preflight OK') 'Cutover preflight failed'
    Log 'SUPERVISED_PREFLIGHT_OK'
    $result = & powershell.exe @opts '-Mode' 'Execute' '-ConfirmExternalSupervisor' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Supervised cutover failed. Output not printed to protect credentials.' }
    Assert ($result -match 'CUTOVER_OK') 'Supervised cutover did not commit'
    Check $v2
    $config = Get-Content -LiteralPath (Join-Path $data 'config\machine.json') -Raw | ConvertFrom-Json
    Assert ($config.previousVersion -ceq $v1 -and $config.previousSequence -eq 701) 'Previous version not kept'
    Log 'SUPERVISED_CUTOVER_OK'
    $null = Inv @('rollback')
    Check $v1
    $null = Inv @('stop')
    $started = $false
    Log 'ROLLBACK_AND_STOP_OK'
    $productAfter = ProductStatus
    Assert ($productAfter -match 'healthy:\s*True' -and $productAfter -match 'ready:\s*True') 'Productive runtime not healthy'
    Assert ($productAfter -match '0.1.0-dev-6b1566a') 'Productive version changed'
    DesktopUnchanged
    $passed = $true
    Log 'RB04_LIVE_TUNNEL_ISOLATED_E2E_OK'
}
catch {
    Log ('RB04_LIVE_TUNNEL_E2E_FAILED: '+$_.Exception.Message)
    throw
}
finally {
    if(Test-Path -LiteralPath $launcher) {
        try {
            $st = & $launcher stop 2>&1 | Out-String
            Log 'CLEANUP_STOP_ATTEMPTED'
        } catch {
            Log 'CLEANUP_STOP_FAILED'
        }
    }
    $uninstaller = Join-Path $install 'unins000.exe'
    if(Test-Path -LiteralPath $uninstaller) {
        try {
            $proc = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru -ErrorAction Stop
            Log ('CLEANUP_UNINSTALL_EXIT='+$proc.ExitCode)
        } catch {
            Log 'CLEANUP_UNINSTALL_FAILED'
        }
    }
    try { DesktopUnchanged;Log 'DESKTOP_SHORTCUTS_UNCHANGED' }
    catch { Log 'DESKTOP_SHORTCUT_INTEGRITY_WARNING'; throw }
    # Preserve a failure log while minimizing test residues.
    $evidence = Join-Path ([IO.Path]::GetTempPath()) ('LoomLCI.RB04.Evidence.' + $id.Substring(0,12) + '.log')
    if(Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination $evidence }
    if(Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Continue
    }
    Write-Host ('RB04_EVIDENCE_LOG='+$evidence)
    Remove-Item Env:\LOOMLCI_INSTALL_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\LOOMLCI_DATA_ROOT -ErrorAction SilentlyContinue
}
