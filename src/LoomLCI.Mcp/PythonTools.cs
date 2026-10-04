using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using LoomLCI.Core;
using LoomLCI.Core.Python;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public sealed record PythonExceptionDto(
    string Type,
    string Message,
    string Traceback);

public sealed record PythonExecutionDto(
    string Status,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    PythonExceptionDto? Exception);

[McpServerToolType]
public sealed class PythonTools
{
    private readonly PythonCapability _python;

    public PythonTools(PythonCapability python)
    {
        _python = python;
    }

    [McpServerTool(
        Name = "python_execute",
        Title = "Execute Python",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PythonExecutionDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description(
        "Executes Python code in the persistent Python worker owned by a work session. " +
        "The first call lazily provisions/starts the private CPython runtime; later calls in the same work session reuse globals, imports, cwd changes, and other in-process state. " +
        "Use this for local calculations, parsing, transformations, and short stateful Python workflows. " +
        "Use LoomLCI filesystem tools for structured file operations and Process tools for independent executables, terminal semantics, subprocess workflows, or large retained output. " +
        "There is no interactive stdin in this version: input() receives EOF. " +
        "A normal Python exception is a successful tool call with result.status='exception'; infrastructure failures, cancellation, and timeouts are tool errors. " +
        "timeoutSeconds covers the whole invocation, including lazy runtime provisioning/startup. On timeout, cancellation, crash, or broken protocol the worker is discarded, so the next call starts with a fresh namespace. " +
        "maxOutputChars applies independently to stdout and stderr and counts Unicode code points; truncation does not stop code execution. Code is limited to 256 KiB when encoded as strict UTF-8.")]
    public async Task<CallToolResult> Execute(
        [Description("Work session handle returned by work_create. Python state is scoped to this session.")] string workId,
        [Description("Python source code to execute. Maximum 256 KiB when encoded as strict UTF-8.")] string code,
        [Description("Maximum duration in seconds for the entire Python invocation, including lazy provisioning/startup.")][Range(1, 600)] int timeoutSeconds = 60,
        [Description("Maximum Unicode code points returned for each of stdout and stderr. Execution continues after this response limit is reached.")][Range(1, 1048576)] int maxOutputChars = 65536,
        CancellationToken cancellationToken = default)
    {
        var result = await _python.ExecuteAsync(
                new PythonExecuteRequest(
                    new WorkId(workId),
                    code,
                    TimeSpan.FromSeconds(timeoutSeconds),
                    maxOutputChars),
                cancellationToken)
            .ConfigureAwait(false);

        return McpToolResults.From(MapExecution(result));
    }

    [McpServerTool(
        Name = "python_reset",
        Title = "Reset Python session",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Discards the persistent Python worker and namespace owned by the work session. " +
        "This is idempotent: it succeeds when no worker exists. The next python_execute starts a fresh worker. " +
        "This is not an interrupt command; if an execution is already active, reset waits for that resource lease to finish before disposing the worker.")]
    public async Task<CallToolResult> Reset(
        [Description("Work session handle whose Python worker should be discarded.")] string workId,
        CancellationToken cancellationToken = default)
    {
        var result = await _python
            .ResetAsync(
                new WorkId(workId),
                cancellationToken)
            .ConfigureAwait(false);

        var envelope = result.IsSuccess
            ? ToolEnvelope<bool>.From(
                LoomResult<bool>.Success(true))
            : ToolEnvelope<bool>.From(
                LoomResult<bool>.Failure(result.Error!));

        return McpToolResults.From(envelope);
    }

    private static ToolEnvelope<PythonExecutionDto> MapExecution(
        LoomResult<PythonExecutionResult> result)
    {
        if (!result.IsSuccess)
        {
            return ToolEnvelope<PythonExecutionDto>.From(
                LoomResult<PythonExecutionDto>.Failure(
                    result.Error!));
        }

        var value = result.Value!;
        var dto = new PythonExecutionDto(
            value.Status switch
            {
                PythonExecutionStatus.Completed => "completed",
                PythonExecutionStatus.Exception => "exception",
                _ => throw new InvalidOperationException(
                    $"Unknown Python execution status '{value.Status}'.")
            },
            value.Stdout,
            value.Stderr,
            value.StdoutTruncated,
            value.StderrTruncated,
            value.Exception is null
                ? null
                : new PythonExceptionDto(
                    value.Exception.Type,
                    value.Exception.Message,
                    value.Exception.Traceback));

        return ToolEnvelope<PythonExecutionDto>.From(
            LoomResult<PythonExecutionDto>.Success(dto));
    }
}
