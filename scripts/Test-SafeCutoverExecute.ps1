#Requires -Version 5.1
# Run this test from the external IvanSpace service, NOT from the Host being tested.
# All paths and test executables live inside a unique disposable TEMP directory.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path ([IO.Path]::GetTempPath()) ('LoomLCI.RB04.Execute.' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'install'
$data = Join-Path $root 'data'
$backup = Join-Path $root 'backups'
$launcher = Join-Path $install 'LoomLCI.Launcher.exe'
$installer = Join-Path $root 'MockInstaller.exe'
$oldHost = Join-Path $install 'versions\v1\LoomLCI.Host.exe'
$newHost = Join-Path $install 'versions\v2\LoomLCI.Host.exe'
$configFile = Join-Path $data 'config\machine.json'
$keyFile = Join-Path $data 'secrets\runtime-api-key.txt'
$scriptFile = Join-Path $PSScriptRoot 'Invoke-SafeCutover.ps1'

New-Item -ItemType Directory -Force -Path (Split-Path $oldHost), (Split-Path $configFile), (Split-Path $keyFile) | Out-Null
try {
    $launcherSource = @'
using System;
using System.IO;
public static class MockLauncher
{
    public static int Main(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("LOOMLCI_CUTOVER_TEST_ROOT");
        if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return 91;
        var statePath = Path.Combine(root, "data", "state.txt");
        if (args.Length == 0) return 92;
        if (args[0] == "stop") { File.WriteAllText(statePath, "stopped"); return 0; }
        if (args[0] == "start") { File.WriteAllText(statePath, "ready"); return 0; }
        if (args[0] == "status")
        {
            bool ready = File.Exists(statePath) && File.ReadAllText(statePath) == "ready";
            var config = File.ReadAllText(Path.Combine(root, "data", "config", "machine.json"));
            string version = config.Contains("\"activeVersion\":\"v2\"") ? "v2" : "v1";
            Console.WriteLine("LoomLCI version: " + version);
            Console.WriteLine("tunnel: tunnel_mock_test");
            Console.WriteLine("process_running: " + (ready ? "True" : "False"));
            Console.WriteLine("healthy: " + (ready ? "True" : "False"));
            Console.WriteLine("ready: " + (ready ? "True" : "False"));
            return ready ? 0 : 1;
        }
        return 93;
    }
}
'@
    $installerSource = @'
using System;
using System.IO;
public static class MockInstaller
{
    public static int Main(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("LOOMLCI_CUTOVER_TEST_ROOT");
        if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return 91;
        var configPath = Path.Combine(root, "data", "config", "machine.json");
        var hostPath = Path.Combine(root, "install", "versions", "v2", "LoomLCI.Host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(hostPath));
        File.WriteAllText(hostPath, "mock-v2-host");
        File.WriteAllText(configPath,
            "{\"activeVersion\":\"v2\",\"activeSequence\":2," +
            "\"previousVersion\":\"v1\",\"previousSequence\":1," +
            "\"highestSequence\":2,\"tunnelId\":\"tunnel_mock_test\"," +
            "\"alias\":\"loomlci-installed\"}");
        return 0;
    }
}
'@
    Add-Type -TypeDefinition $launcherSource -OutputAssembly $launcher -OutputType ConsoleApplication -ErrorAction Stop
    Add-Type -TypeDefinition $installerSource -OutputAssembly $installer -OutputType ConsoleApplication -ErrorAction Stop

    [IO.File]::WriteAllText($oldHost, 'mock-v1-host')
    [IO.File]::WriteAllText($keyFile, 'isolated-fake-key')
    [IO.File]::WriteAllText($configFile,
        '{"activeVersion":"v1","activeSequence":1,"previousSequence":0,"highestSequence":1,"tunnelId":"tunnel_mock_test","alias":"loomlci-installed"}')
    [IO.File]::WriteAllText((Join-Path $data 'state.txt'), 'ready')
    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $launcherHash = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    # Hash the exact bytes that the mock installer will publish.
    $hashTemp = Join-Path $root 'expected-host.bin'
    [IO.File]::WriteAllText($hashTemp, 'mock-v2-host')
    $hostHash = (Get-FileHash -LiteralPath $hashTemp -Algorithm SHA256).Hash

    $metadataPath = [IO.Path]::ChangeExtension($installer, 'deployment.json')
    $metadata = [ordered]@{
        schemaVersion = 1
        installerFormat = 'inno-setup7'
        version = 'v2'
        sequence = 2
        installRoot = $install
        dataRoot = $data
        installerSha256 = $installerHash
    }
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 4))

    $env:LOOMLCI_CUTOVER_TEST_ROOT = $root
    $args = @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$scriptFile,
        '-Mode','Execute',
        '-InstallerPath',$installer,'-InstallerSha256',$installerHash,
        '-TargetVersion','v2','-TargetSequence','2',
        '-ExpectedCurrentVersion','v1','-ExpectedCurrentSequence','1',
        '-InstallRoot',$install,'-DataRoot',$data,'-BackupRoot',$backup,
        '-ExpectedLauncherSha256',$launcherHash,
        '-ExpectedHostSha256',$hostHash,
        '-ConfirmExternalSupervisor')
    $result = & powershell.exe @args 2>&1 | Out-String
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
        throw "Isolated external execute failed (exit $exit): $result"
    }
    if ((Get-Content -LiteralPath (Join-Path $data 'state.txt') -Raw) -ne 'ready') {
        throw "Runtime test state not ready"
    }
    $installed = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    if ($installed.activeVersion -ne 'v2' -or $installed.previousVersion -ne 'v1') {
        throw 'Incorrect updated active/previous version'
    }
    if ((Get-Content -LiteralPath $oldHost -Raw) -ne 'mock-v1-host' -or
        (Get-Content -LiteralPath $newHost -Raw) -ne 'mock-v2-host') {
        throw 'Host rollback/or newly installed bytes do not match'
    }
    $logs = @(Get-ChildItem -LiteralPath $backup -Filter 'cutover.log' -Recurse -File)
    if ($logs.Count -ne 1 -or
        (Get-Content -LiteralPath $logs[0].FullName -Raw) -notmatch 'CUTOVER_OK') {
        throw 'Missing cutover backup or successful audit log'
    }
    Write-Host 'RB04_EXTERNAL_ISOLATED_EXECUTE_OK'
}
finally {
    Remove-Item Env:\LOOMLCI_CUTOVER_TEST_ROOT -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
