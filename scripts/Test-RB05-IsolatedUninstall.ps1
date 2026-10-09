#Requires -Version 5.1
# RB-05: a genuine legacy Inno uninstall log must not delete foreign user data
# after upgrade to the current installer. All roots and AppIds are isolated.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$token = [Guid]::NewGuid().ToString('N')
$root = Join-Path $env:TEMP ('LoomLCI.RB05.E2E.' + $token.Substring(0,12))
$install = Join-Path $root 'install'
$loom = Join-Path $root 'loom'
$data = Join-Path $loom 'deployment'
$out = Join-Path $root 'out'
$portable = Join-Path $root 'portable'
$appId = 'LoomLCI.RB05.E2E.' + $token
$legacy = Join-Path $root 'legacy.iss'
$legacyPayload = Join-Path $root 'legacy.txt'
$key = Join-Path $env:LOCALAPPDATA 'LoomLCI\http-test\secrets\runtime-api-key.txt'
$tunnelId = 'tunnel_6ac73f6a8cfc8191b551818597fc5413'
$sentinel = Join-Path $loom 'http-test\DO_NOT_DELETE.txt'
$preserved = Join-Path $data 'preserve.txt'
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$shortcuts = @('LoomLCI.lnk','Detener LoomLCI.lnk')
$shortcutsBefore = @{}
$testPassed = $false
foreach ($name in $shortcuts) {
    $p = Join-Path $desktop $name
    $shortcutsBefore[$name] = if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { '' }
}
New-Item -ItemType Directory -Path $install, $loom, (Split-Path $sentinel), $data, $out -Force | Out-Null
try {
    [IO.File]::WriteAllText($legacyPayload, 'legacy install payload')
    if (-not (Test-Path -LiteralPath $key -PathType Leaf) -or (Get-Item -LiteralPath $key).Length -eq 0) { throw 'Isolated HTTP test credential unavailable' }
    [IO.File]::WriteAllText($sentinel, 'foreign user data survives')
    [IO.File]::WriteAllText($preserved, 'deployment persists')
    $inno = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'
    if (-not (Test-Path -LiteralPath $inno)) { throw 'Inno 7.1.0 unavailable' }
    $source = @(
      '[Setup]',
      ('AppId='+$appId),
      'AppName=LoomLCI',
      'AppVersion=legacy',
      ('DefaultDirName='+$install),
      'DisableDirPage=yes',
      'PrivilegesRequired=lowest',
      'ArchitecturesAllowed=x64compatible',
      'ArchitecturesInstallIn64BitMode=x64compatible',
      ('OutputDir='+$out),
      'OutputBaseFilename=legacy-setup',
      'Uninstallable=yes',
      '[Files]',
      ('Source: "'+$legacyPayload+'"; DestDir: "{app}"; Flags: ignoreversion'),
      '[UninstallDelete]',
      ('Type: filesandordirs; Name: "'+$loom+'"')
    )
    [IO.File]::WriteAllLines($legacy, [string[]]$source, (New-Object System.Text.UTF8Encoding($false)))
    & $inno $legacy *> (Join-Path $root 'legacy-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Legacy installer build failed' }
    $legacySetup = Join-Path $out 'legacy-setup.exe'
    $old = Start-Process -FilePath $legacySetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($old.ExitCode -ne 0) { throw "Legacy install failed: $($old.ExitCode)" }
    Write-Output 'RB05_LEGACY_INSTALL_OK'

    # Real current Inno script, unique roots/AppId, dummy nonsecret key.
    $realTunnel = Join-Path $env:LOCALAPPDATA 'Programs\LoomLCI\tools\tunnel-client.exe'
    $expected = 'fcc85a69ec0ad82518e4f8964f60c45e31787957782a0fc9c1b0c44e82d61b9b'
    if ((Get-FileHash -LiteralPath $realTunnel -Algorithm SHA256).Hash -ine $expected) {
        throw 'Pinned tunnel-client hash mismatch'
    }
    New-Item -ItemType Directory -Path (Join-Path $install 'tools') -Force | Out-Null
    Copy-Item -LiteralPath $realTunnel -Destination (Join-Path $install 'tools\tunnel-client.exe')
    $args = @{
        OutputRoot=$out; PortableOutputRoot=$portable;
        Version='0.1.0-dev-rb05-e2e'; Sequence=991;
        InstallRoot=$install; LoomRoot=$loom; AppId=$appId;
        SkipPortableTests=$true; SkipPortableZip=$true
    }
    & (Join-Path $repo 'scripts\Build-WindowsInstaller.ps1') @args *> (Join-Path $root 'new-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Current installer build failed' }
    Write-Output 'RB05_NEW_INSTALLER_BUILT'
    $setup = Join-Path $out 'LoomLCI-0.1.0-dev-rb05-e2e-win-x64-setup.exe'
    $new = Start-Process -FilePath $setup -ArgumentList @(
        '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        ('/TUNNELID='+$tunnelId),
        ('/RUNTIMEKEYFILE="'+$key+'"'),
        '/NOSHORTCUTS=1') -Wait -PassThru
    if ($new.ExitCode -ne 0) { throw "Expected successful isolated setup; exit $($new.ExitCode)" }
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Setup deleted foreign data' }
    Write-Output 'RB05_ISOLATED_REAL_SETUP_SUCCEEDED'

    # Demonstrate a failed stop aborts BEFORE any uninstall cleanup.
    $config = Join-Path $data 'config\machine.json'
    New-Item -ItemType Directory -Path (Split-Path $config) -Force | Out-Null
    $configBackup = Join-Path $root 'machine-backup.json'
    Copy-Item -LiteralPath $config -Destination $configBackup -ErrorAction Stop
    [IO.File]::WriteAllText($config, 'corrupt test-only machine config')
    $uninstallers = @(Get-ChildItem -LiteralPath $install -Filter 'unins*.exe' -File)
    Write-Output ('RB05_UNINSTALLER_COUNT=' + $uninstallers.Count)
    if ($uninstallers.Count -ne 1) { throw 'Migration produced multiple uninstall executables; unsafe to proceed' }
    $uninstaller = $uninstallers[0].FullName
    $deniedLog = Join-Path $root 'denied-uninstall.log'
    $denied = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$deniedLog+'"')) -Wait -PassThru
    Write-Output ('RB05_DENIED_UNINSTALL_EXIT_CODE=' + $denied.ExitCode)
    Write-Output ('RB05_DENIED_UNINSTALLER_PRESENT=' + (Test-Path -LiteralPath $uninstaller))
    Write-Output ('RB05_DENIED_SENTINEL_PRESENT=' + (Test-Path -LiteralPath $sentinel))
    if (Test-Path -LiteralPath $deniedLog) { Get-Content -LiteralPath $deniedLog -Tail 28 | ForEach-Object { Write-Output ('UNINSTALL_LOG: '+$_) } }
    if (-not (Test-Path -LiteralPath $uninstaller) -or -not(Test-Path -LiteralPath $sentinel)) {
        throw 'Failed stop partially uninstalled or deleted data'
    }
    Write-Output 'RB05_FAILED_STOP_BLOCKED_UNINSTALL'
    Copy-Item -LiteralPath $configBackup -Destination $config -Force

    $un = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($un.ExitCode -ne 0) { throw "Uninstall failed: $($un.ExitCode)" }
    if (-not(Test-Path -LiteralPath $sentinel) -or -not(Test-Path -LiteralPath $preserved)) {
        throw 'Inherited legacy log deleted preserved files!'
    }
    # Overwrite deliberately drops prior log entries: unrelated/legacy files are never deleted by name guessing.
    if (-not (Test-Path -LiteralPath (Join-Path $install 'legacy.txt'))) { throw 'Unknown legacy payload unexpectedly deleted' }
    foreach ($name in $shortcuts) {
        $p = Join-Path $desktop $name
        $now = if(Test-Path -LiteralPath $p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}else{''}
        if($now -ne $shortcutsBefore[$name]) {throw "Production desktop shortcut modified: $name"}
    }
    Write-Output 'RB05_LEGACY_LOG_MIGRATION_PRESERVED_DATA'
    Write-Output 'RB05_FOREIGN_SHORTCUTS_UNCHANGED'
    # Install again over the preserved deployment; verify configuration and data survive.
    New-Item -ItemType Directory -Path (Join-Path $install 'tools') -Force | Out-Null
    Copy-Item -LiteralPath $realTunnel -Destination (Join-Path $install 'tools\tunnel-client.exe') -Force
    $again = Start-Process -FilePath $setup -ArgumentList @(
        '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        ('/TUNNELID='+$tunnelId),
        ('/RUNTIMEKEYFILE="'+$key+'"'),
        '/NOSHORTCUTS=1') -Wait -PassThru
    if ($again.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $config) -or
        -not(Test-Path -LiteralPath $preserved) -or -not(Test-Path -LiteralPath $sentinel)) {
        throw "Reinstall did not preserve deployment (exit $($again.ExitCode))"
    }
    Write-Output 'RB05_REINSTALL_PRESERVED_DATA'

    # Explicit destructive operation targets ONLY the marked deployment.
    $env:LOOMLCI_INSTALL_ROOT = $install
    $env:LOOMLCI_DATA_ROOT = $data
    try {
        & (Join-Path $install 'LoomLCI.Launcher.exe') 'purge-data' '--confirm-erase-deployment' *> (Join-Path $root 'purge.log')
        if ($LASTEXITCODE -ne 0) { throw 'Explicit isolated purge command failed' }
    }
    finally {
        Remove-Item Env:\LOOMLCI_INSTALL_ROOT -ErrorAction SilentlyContinue
        Remove-Item Env:\LOOMLCI_DATA_ROOT -ErrorAction SilentlyContinue
    }
    if ((Test-Path -LiteralPath $data) -or -not(Test-Path -LiteralPath $sentinel)) {
        throw 'Explicit purge did not preserve sibling data'
    }
    Write-Output 'RB05_EXPLICIT_PURGE_SCOPED_OK'
    $uninstallAgain = @(Get-ChildItem -LiteralPath $install -Filter 'unins*.exe' -File)
    if ($uninstallAgain.Count -ne 1) { throw 'Reinstall generated ambiguous uninstall logs' }
    $last = Start-Process -FilePath $uninstallAgain[0].FullName -ArgumentList @(
        '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($last.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $sentinel)) {
        throw 'Post-purge uninstall failed or deleted shared data'
    }
    Write-Output 'RB05_POST_PURGE_UNINSTALL_OK'
    Write-Output 'RB05_GENUINE_E2E_PASS'
    $testPassed = $true
}
finally {
    if($testPassed -and (Test-Path -LiteralPath $root)) {
        # Only an isolated GUID root, never a production path.
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    } elseif(-not $testPassed) { Write-Output ('RB05_DEBUG_ROOT='+$root) }
}
