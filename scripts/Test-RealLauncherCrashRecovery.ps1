#Requires -Version 5.1
# External parent actually kills a separate .NET 10 process while it is paused
# inside a persisted journal transition, then starts a NEW process to recover.
# Strict TEMP-root guard; no installed Launcher or real tunnel is invoked.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exe = Join-Path $PSScriptRoot '..\tests\LoomLCI.Launcher.CrashHarness\bin\Release\net10.0-windows\LoomLCI.Launcher.CrashHarness.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Build crash harness first: $exe" }

$parent = Join-Path ([IO.Path]::GetTempPath()) ('LoomLCI.RB04.Crash.' + [guid]::NewGuid().ToString('N'))
$operationTemps = New-Object 'System.Collections.Generic.HashSet[string]'
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$passed = 0

function Invoke-Harness([string[]]$arguments) {
    $output = & $exe @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "CrashHarness failed ($LASTEXITCODE): $($arguments -join ' ') output: $output"
    }
    return $output
}

function Kill-AtStage([string]$root, [string]$command, [string]$stage) {
    $marker = Join-Path $root 'probe.ready'
    if (Test-Path $marker) { Remove-Item $marker -Force }
    $p = Start-Process -FilePath $exe -ArgumentList @($command, $root, $stage) -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while (-not (Test-Path -LiteralPath $marker)) {
            $p.Refresh()
            if ($p.HasExited) { throw "Child exited before crash checkpoint $stage, exit $($p.ExitCode)" }
            if ([DateTime]::UtcNow -gt $deadline) { throw "Child never reached crash checkpoint $stage" }
            Start-Sleep -Milliseconds 100
        }
        $actual = Get-Content -LiteralPath $marker -Raw
        if ($actual -ne $stage) { throw "Unexpected crash marker: $actual" }
        $p.Refresh()
        if ($p.HasExited) { throw "Child exited before it could be killed" }
        Stop-Process -Id $p.Id -Force
        if (-not $p.WaitForExit(10000)) { throw "Killed process did not exit" }
        if ($p.ExitCode -eq 0) { throw "Child exited successfully; this was not an abrupt kill" }
    }
    finally {
        $p.Refresh()
        if (-not $p.HasExited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
        $p.Dispose()
    }

    $journalFile = Join-Path $root 'data\update\journal.json'
    if (-not (Test-Path -LiteralPath $journalFile)) { throw "Journal missing after process kill" }
    $journal = Get-Content -LiteralPath $journalFile -Raw | ConvertFrom-Json
    if ($journal.schemaVersion -ne 2 -or $journal.operation -ne 'update') {
        throw "Invalid persisted v2 journal after process kill"
    }
    $operationId = [string]$journal.operationId
    if ($operationId -notmatch '^[0-9a-f]{32}$') { throw "Invalid operation id in journal" }
    [void]$operationTemps.Add((Join-Path ([IO.Path]::GetTempPath()) ("LoomLCI.Update\" + $operationId)))
}

try {
    $stages = @(
        'prepared', 'promoting', 'backed_up', 'published',
        'stopping', 'runtime_stopped', 'config_saved',
        'activated', 'runtime_started', 'committed'
    )
    foreach ($stage in $stages) {
        $case = Join-Path $parent $stage
        New-Item -ItemType Directory -Path $case | Out-Null
        $null = Invoke-Harness @('init', $case)
        Kill-AtStage -root $case -command 'apply' -stage $stage
        $null = Invoke-Harness @('recover', $case)
        $expected = if ($stage -in @('activated','runtime_started','committed')) { 'v2' } else { 'v1' }
        $null = Invoke-Harness @('verify', $case, $expected)
        Write-Host "RB04_PROCESS_KILL_RECOVERY_PASS $stage -> $expected"
        $passed++
    }

    $case = Join-Path $parent 'recover-killed'
    New-Item -ItemType Directory -Path $case | Out-Null
    $null = Invoke-Harness @('init', $case)
    Kill-AtStage -root $case -command 'apply' -stage 'published'
    Kill-AtStage -root $case -command 'recover' -stage 'restored_files'
    $null = Invoke-Harness @('recover', $case)
    $null = Invoke-Harness @('verify', $case, 'v1')
    Write-Host 'RB04_PROCESS_KILL_RECOVERY_PASS interrupted_restore -> v1'
    $passed++

    if ($passed -ne 11) { throw "Unexpected number of recovery cases: $passed" }
    Write-Host "RB04_REAL_PROCESS_KILL_RECOVERY_OK ($passed/11)"
}
finally {
    foreach ($operation in $operationTemps) {
        if (Test-Path -LiteralPath $operation) {
            Remove-Item -LiteralPath $operation -Recurse -Force -ErrorAction Continue
        }
    }
    if (Test-Path -LiteralPath $parent) {
        Remove-Item -LiteralPath $parent -Recurse -Force -ErrorAction Continue
    }
}
