#Requires -Version 5.1
<#
  Early MCP contract gate for local development and package construction.
  Always builds the Host from current sources; it never targets a preexisting
  published Host and is not a replacement for full release/integration tests.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BuildStageTiming.ps1')

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostProject = Join-Path $repoRoot 'src\LoomLCI.Host\LoomLCI.Host.csproj'
$testsProject = Join-Path $repoRoot 'tests\LoomLCI.IntegrationTests\LoomLCI.IntegrationTests.csproj'
$targetFramework = (& dotnet msbuild $hostProject '-getProperty:TargetFramework' '-p:Configuration=Release').Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($targetFramework) -or
    $targetFramework -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Fast MCP contract gate: no se pudo resolver TargetFramework del Host.'
}
$hostDll = Join-Path $repoRoot ("src\LoomLCI.Host\bin\Release\$targetFramework\LoomLCI.Host.dll")
$filter = 'FullyQualifiedName~StdioAdapterExposesSelfDescribingToolContracts|FullyQualifiedName~PluginContractSnapshotMatchesAdvertisedTools'
$total = [System.Diagnostics.Stopwatch]::StartNew()

try {
    Invoke-DxStage 'fast-host-build' {
        & dotnet build $hostProject -c Release --no-restore --verbosity quiet
        if ($LASTEXITCODE -ne 0) {
            throw 'Fast MCP contract gate: falló la compilación Release del Host.'
        }
    }

    if (-not (Test-Path -LiteralPath $hostDll -PathType Leaf)) {
        throw "Fast MCP contract gate: falta Host recién compilado en $hostDll."
    }

    Invoke-DxStage 'fast-mcp-contracts' {
        $previousHost = $env:LOOMLCI_TEST_HOST_DLL
        try {
            # Explicitly bound to the just-built Host; no DEBUG or installed Host.
            $env:LOOMLCI_TEST_HOST_DLL = $hostDll
            & dotnet test $testsProject -c Release --no-restore --filter $filter --verbosity quiet
            if ($LASTEXITCODE -ne 0) {
                throw 'Fast MCP contract gate: la descripción o snapshot MCP no coincide.'
            }
        }
        finally {
            $env:LOOMLCI_TEST_HOST_DLL = $previousHost
        }
    }

    Write-Host 'DX01_FAST_CONTRACTS_OK (solo prevalidacion; NO autoriza un deployment)'
}
finally {
    $total.Stop()
    Write-Host ("DX01_STAGE name=fast-total elapsedMs={0}" -f $total.ElapsedMilliseconds)
}
