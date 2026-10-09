# Shared diagnostic timer for the development/package workflow.
# Timings are telemetry, not a substitute for exit codes or tests.
function Invoke-DxStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'ok'
    try {
        & $Action
    }
    catch {
        $status = 'failed'
        throw
    }
    finally {
        $timer.Stop()
        Write-Host ("DX01_STAGE name={0} elapsedMs={1} status={2}" -f
            $Name, $timer.ElapsedMilliseconds, $status)
    }
}
