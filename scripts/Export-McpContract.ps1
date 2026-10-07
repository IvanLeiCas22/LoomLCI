[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$HostPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Write-RpcMessage {
    param(
        [Parameter(Mandatory)]$Writer,
        [Parameter(Mandatory)]$Message
    )

    $json = $Message | ConvertTo-Json -Depth 100 -Compress
    $Writer.WriteLine($json)
    $Writer.Flush()
}

function Read-RpcResponse {
    param(
        [Parameter(Mandatory)]$Process,
        [Parameter(Mandatory)][int]$Id,
        [Parameter(Mandatory)][DateTime]$Deadline
    )

    while ([DateTime]::UtcNow -lt $Deadline) {
        $remaining = [int][Math]::Max(
            1,
            ($Deadline - [DateTime]::UtcNow).TotalMilliseconds)

        $task = $Process.StandardOutput.ReadLineAsync()
        if (-not $task.Wait($remaining)) {
            throw "Timeout esperando respuesta MCP id=$Id."
        }

        $line = $task.Result
        if ($null -eq $line) {
            $stderr = $Process.StandardError.ReadToEnd()
            throw "El Host cerró stdout antes de responder id=$Id. stderr: $stderr"
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $message = $line | ConvertFrom-Json
        }
        catch {
            throw "El Host emitió una línea no JSON por stdout: $line"
        }

        if ($null -ne $message.id -and [int]$message.id -eq $Id) {
            if ($null -ne $message.error) {
                throw "MCP id=$Id devolvió error: $($message.error | ConvertTo-Json -Depth 20 -Compress)"
            }
            return $message
        }
    }

    throw "Timeout esperando respuesta MCP id=$Id."
}

$HostPath = [System.IO.Path]::GetFullPath($HostPath)
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

if (-not (Test-Path $HostPath -PathType Leaf)) {
    throw "No existe HostPath: $HostPath"
}
if ([System.IO.Path]::GetExtension($HostPath) -ne '.exe') {
    throw 'HostPath debe apuntar a un LoomLCI.Host.exe publicado.'
}
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 120) {
    throw 'TimeoutSeconds debe estar entre 1 y 120.'
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $HostPath
$startInfo.WorkingDirectory = Split-Path -Parent $HostPath
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo

try {
    if (-not $process.Start()) {
        throw "No se pudo iniciar $HostPath"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    Write-RpcMessage -Writer $process.StandardInput -Message ([ordered]@{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = [ordered]@{
            protocolVersion = '2025-11-25'
            capabilities = @{}
            clientInfo = [ordered]@{
                name = 'loomlci-plugin-contract-exporter'
                version = '1.0'
            }
        }
    })

    $initialize = Read-RpcResponse -Process $process -Id 1 -Deadline $deadline

    Write-RpcMessage -Writer $process.StandardInput -Message ([ordered]@{
        jsonrpc = '2.0'
        method = 'notifications/initialized'
        params = @{}
    })

    $allTools = @()
    $cursor = $null
    $requestId = 2

    do {
        $params = [ordered]@{}
        if (-not [string]::IsNullOrWhiteSpace($cursor)) {
            $params.cursor = $cursor
        }

        Write-RpcMessage -Writer $process.StandardInput -Message ([ordered]@{
            jsonrpc = '2.0'
            id = $requestId
            method = 'tools/list'
            params = $params
        })

        $response = Read-RpcResponse -Process $process -Id $requestId -Deadline $deadline
        $allTools += @($response.result.tools)
        $cursor = $response.result.nextCursor
        $requestId++
    }
    while (-not [string]::IsNullOrWhiteSpace($cursor))

    $normalizedTools = @(
        $allTools |
            Sort-Object name |
            ForEach-Object {
                $tool = $_
                $entry = [ordered]@{
                    name = $tool.name
                }

                if ($null -ne $tool.title) {
                    $entry.title = $tool.title
                }
                if ($null -ne $tool.description) {
                    $entry.description = $tool.description
                }

                $entry.inputSchema = $tool.inputSchema

                if ($tool.PSObject.Properties.Name -contains 'outputSchema') {
                    $entry.outputSchema = $tool.outputSchema
                }
                if ($tool.PSObject.Properties.Name -contains 'annotations') {
                    $entry.annotations = $tool.annotations
                }

                [pscustomobject]$entry
            }
    )

    $snapshot = [ordered]@{
        schemaVersion = 1
        protocolVersion = $initialize.result.protocolVersion
        serverInfo = $initialize.result.serverInfo
        serverInstructions = $initialize.result.instructions
        tools = $normalizedTools
    }

    $json = $snapshot | ConvertTo-Json -Depth 100
    Write-Utf8NoBom -Path $OutputPath -Text ($json + [Environment]::NewLine)

    Write-Host "Exported $($normalizedTools.Count) MCP tools to $OutputPath"
}
finally {
    if (-not $process.HasExited) {
        try {
            $process.Kill()
        }
        catch {
        }
    }

    try {
        $process.WaitForExit(5000) | Out-Null
    }
    catch {
    }

    $process.Dispose()
}
