#Requires -Version 5.1
# Builds and executes a genuine Inno Setup installer with a unique TEMP AppId,
# isolated install/data roots, and fake credentials. No product tunnel touched.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) ("LoomLCI.R4.I." + $id.Substring(0,12))
$install = Join-Path $root 'install'
$loom = Join-Path $root 'loom'
$data = Join-Path $loom 'deployment'
$out = Join-Path $root 'out'
$portable = Join-Path $root 'portable'
$version = "r4-i-" + $id.Substring(0,12)
$sequence = 901
$appId = "LoomLCI.RB04.Isolated.$id"
$keyPath = Join-Path $root 'test-key.txt'
$installerLog = Join-Path $root 'installer.log'
$installedLauncher = Join-Path $install 'LoomLCI.Launcher.exe'
$realTunnelClient = Join-Path $env:LOCALAPPDATA 'Programs\LoomLCI\tools\tunnel-client.exe'
$setupCode = $null
$uninstallCode = $null
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$shortcutBaseline = @{}
foreach ($name in @('LoomLCI.lnk', 'Detener LoomLCI.lnk')) {
    $shortcutPath = Join-Path $desktop $name
    $shortcutBaseline[$shortcutPath] = if (Test-Path -LiteralPath $shortcutPath) {
        (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash
    } else {
        $null
    }
}

New-Item -ItemType Directory -Path $root, (Join-Path $install 'tools') -Force | Out-Null
try {
    # Read-only source from productive installation: only the pinned binary.
    $expectedTunnelHash = 'fcc85a69ec0ad82518e4f8964f60c45e31787957782a0fc9c1b0c44e82d61b9b'
    $actualTunnelHash = (Get-FileHash -LiteralPath $realTunnelClient -Algorithm SHA256).Hash
    if ($actualTunnelHash -ine $expectedTunnelHash) {
        throw 'Original pinned tunnel binary has unexpected digest'
    }
    Copy-Item -LiteralPath $realTunnelClient -Destination (Join-Path $install 'tools\tunnel-client.exe') -ErrorAction Stop
    [IO.File]::WriteAllText($keyPath, 'rb04-fake-nonsecret-test-credential')

    $buildArgs = @{
        OutputRoot = $out
        PortableOutputRoot = $portable
        Version = $version
        Sequence = $sequence
        InstallRoot = $install
        LoomRoot = $loom
        AppId = $appId
        SkipPortableTests = $true
    }
    $buildLog = Join-Path $root 'build.log'
    try {
        & (Join-Path $repo 'scripts\Build-WindowsInstaller.ps1') @buildArgs *> $buildLog
        if ($LASTEXITCODE -ne 0) { throw 'Genuine isolated Inno Setup build returned nonzero exit code' }
    }
    catch {
        Write-Host 'RB04_GENUINE_INSTALLER_BUILD_FAILED'
        if (Test-Path -LiteralPath $buildLog) {
            Get-Content -LiteralPath $buildLog -Tail 30 | ForEach-Object { Write-Host $_ }
        }
        throw
    }
    Write-Host 'RB04_GENUINE_INSTALLER_BUILD_OK'

    $setup = Join-Path $out ("LoomLCI-" + $version + "-win-x64-setup.exe")
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer missing' }
    $metadata = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($setup, 'deployment.json')) -Raw | ConvertFrom-Json
    if ($metadata.version -ne $version -or $metadata.sequence -ne $sequence -or
        $metadata.installRoot -ine $install -or $metadata.dataRoot -ine $data -or
        $metadata.installerSha256 -ine (Get-FileHash $setup -Algorithm SHA256).Hash) {
        throw 'Genuine installer metadata failed verification'
    }

    $tunnelId = 'tunnel_rb04_test_only_' + $id
    $setupArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        ('/LOG="' + $installerLog + '"'),
        ('/TUNNELID=' + $tunnelId),
        ('/RUNTIMEKEYFILE="' + $keyPath + '"'),
        '/NOSHORTCUTS=1')
    Write-Host 'Executing genuine installer with unique TEMP install/data roots and dummy credentials'
    $proc = Start-Process -FilePath $setup -ArgumentList $setupArgs -Wait -PassThru -ErrorAction Stop
    $setupCode = $proc.ExitCode
    Write-Host "RB04_GENUINE_INSTALLER_EXIT_CODE=$setupCode"

    if (-not (Test-Path -LiteralPath $installedLauncher -PathType Leaf)) {
        throw 'Genuine setup did not install its Launcher payload in isolated directory'
    }
    $packageLauncher = Join-Path $portable ("LoomLCI-$version-win-x64\LoomLCI.Launcher.exe")
    $packageHost = Join-Path $portable ("LoomLCI-$version-win-x64\payload\host\LoomLCI.Host.exe")
    $installedHost = Join-Path $install ("versions\$version\LoomLCI.Host.exe")
    if (-not (Test-Path -LiteralPath $installedHost) -or
        (Get-FileHash $installedLauncher -Algorithm SHA256).Hash -ine
            (Get-FileHash $packageLauncher -Algorithm SHA256).Hash -or
        (Get-FileHash $installedHost -Algorithm SHA256).Hash -ine
            (Get-FileHash $packageHost -Algorithm SHA256).Hash) {
        throw 'Genuine installed binaries differ from the validated portable'
    }

    $configPath = Join-Path $data 'config\machine.json'
    if ($setupCode -eq 0) {
        if (-not (Test-Path -LiteralPath $configPath)) {
            Write-Host 'RB04_CONFIG_ABSENT_DIAGNOSTICS:'
            if (Test-Path -LiteralPath $installerLog) {
                Get-Content -LiteralPath $installerLog -Tail 70 |
                    ForEach-Object { Write-Host $_ }
            }
            if (Test-Path -LiteralPath $data) {
                Get-ChildItem -LiteralPath $data -Recurse -File |
                    Select-Object -First 35 -ExpandProperty FullName |
                    ForEach-Object { Write-Host ("Isolated-file: " + $_) }
            }
            throw 'Installer reported success without machine.json'
        }
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        if ($config.activeVersion -cne $version -or
            [long]$config.activeSequence -ne $sequence -or
            $config.tunnelId -cne $tunnelId) {
            throw 'Genuine setup did not configure exact isolated version'
        }
        Write-Host 'RB04_GENUINE_INSTALLER_CONFIG_OK'
    }
    else {
        if ($setupCode -ne 100) {
            throw "Setup failed with unexpected code $setupCode (expected 100 on rejected dummy credentials)"
        }
        if (Test-Path -LiteralPath $configPath) {
            throw 'Failed setup unexpectedly published machine.json'
        }
        Write-Host 'RB04_GENUINE_INSTALLER_REJECTED_DUMMY_CREDENTIALS_CORRECTLY'
    }
    Write-Host 'RB04_GENUINE_INSTALLER_PAYLOAD_AND_PATHS_VERIFIED'
}
finally {
    # Unique AppId and compiled roots: uninstall only this test installation.
    $uninstaller = Join-Path $install 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        try {
            $up = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru -ErrorAction Stop
            $uninstallCode = $up.ExitCode
            Write-Host "RB04_ISOLATED_UNINSTALL_EXIT_CODE=$uninstallCode"
        } catch {
            Write-Warning ('Isolated uninstall failed: ' + $_.Exception.Message)
        }
    }
    foreach ($shortcutPath in $shortcutBaseline.Keys) {
        $originalHash = $shortcutBaseline[$shortcutPath]
        $nowExists = Test-Path -LiteralPath $shortcutPath
        if ($null -ne $originalHash) {
            if (-not $nowExists -or
                (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash -ine $originalHash) {
                throw "Isolated uninstall unexpectedly modified Desktop shortcut: $shortcutPath"
            }
        }
        elseif ($nowExists) {
            throw "Isolated uninstall unexpectedly created Desktop shortcut: $shortcutPath"
        }
    }
    Write-Host 'RB04_DESKTOP_SHORTCUTS_UNCHANGED'
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Continue
    }
}
