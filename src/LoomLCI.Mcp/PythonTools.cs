using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using LoomLCI.Core;
using LoomLCI.Core.Python;
using LoomLCI.Core.VisualFiles;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public sealed record PythonExceptionDto(
    string Type,
    string Message,
    string Traceback);

public sealed record PythonExecutionOutputDto(
    string Kind,
    string MimeType,
    long SizeBytes);

public sealed record PythonExecutionDto(
    string Status,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    PythonExceptionDto? Exception,
    IReadOnlyList<PythonExecutionOutputDto> Outputs);

public sealed record PythonPackageRequirementInputDto(
    [property: Description("PyPI project name. Direct URLs, paths, Git/VCS and requirement options are not accepted.")]
    [property: MinLength(1)]
    [property: MaxLength(128)]
    [property: RegularExpression(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$")]
    string Name,
    [property: Description("Optional exact package version. Omit to resolve the current compatible stable version.")]
    [property: MaxLength(128)]
    [property: RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9.!+_-]{0,127}$")]
    string? Version = null);

public sealed record PythonResolvedPackageDto(
    string Name,
    string Version);

public sealed record PythonPackagesPrepareDto(
    string? EnvironmentId,
    string PythonVersion,
    IReadOnlyList<PythonResolvedPackageDto> Packages,
    bool Reused,
    bool WorkerRestartRequired);

[McpServerToolType]
public sealed class PythonTools
{
    private readonly PythonCapability _python;
    private readonly PythonPackageCapability _packages;

    public PythonTools(
        PythonCapability python,
        PythonPackageCapability packages)
    {
        _python = python;
        _packages = packages;
    }

    [McpServerTool(
        Name = "python_execute",
        Title = "Execute Python",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description(
        "Executes Python code in the persistent Python worker owned by a work session. " +
        "The first call lazily provisions/starts the private CPython runtime; later calls in the same work session reuse globals, imports, cwd changes, and other in-process state. " +
        "Use this for local calculations, parsing, transformations, and short stateful Python workflows. " +
        "Inside the worker, import loom, loom.fs, or loom.process to use LoomLCI capabilities against the same work session. loom.fs provides structured filesystem operations (list/search/read/patch/directories/textual PDF). loom.process provides Loom-managed run/start/status/read/write/resize/terminate/release with Job Objects, retained output and terminal support; durable processes created there are always session-owned. Normal bridge failures raise loom.LoomError and do not discard the worker. " +
        "Use loom.display_image(bytes|bytearray|memoryview) for PNG, JPEG, or WebP images generated in memory during this Python execution. Up to 4 image outputs are allowed with 6 MiB raw aggregate, and the complete MCP result (text, metadata, and images) must fit a 9 MiB serialized response budget. " +
        "Use filesystem_view_image for image files that already exist and filesystem_render_pdf_page for existing PDF pages instead of loading those files into Python just to display them. " +
        "Invalid image format or a final result over the MCP budget is reported after Python has already completed with executionCompleted=true; worker globals and side effects remain, so do not automatically retry the same code. " +
        "There is no interactive stdin in this version: input() receives EOF. " +
        "A normal Python exception is a successful tool call with result.status='exception' and can preserve images emitted before the exception; infrastructure failures, cancellation, and timeouts are tool errors. " +
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

        return BuildExecutionToolResult(result);
    }

    [McpServerTool(
        Name = "python_packages_prepare",
        Title = "Prepare Python packages",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PythonPackagesPrepareDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description(
        "Prepares the complete desired third-party package set for a work session using LoomLCI's private Python runtime and package store. " +
        "Packages are resolved only from the official PyPI index, installed from wheels only, locked to exact versions with hashes, and kept outside the immutable CPython runtime. " +
        "Pass the full desired direct-package set on every call; an empty array switches the session back to base Python with no third-party environment. " +
        "Package names and optional exact versions are structured inputs; direct URLs, Git/VCS, local paths, editable installs, private indexes and source builds are not supported. " +
        "Prepared environments are reusable across work sessions. If a Python worker is already alive with a different environment, the call succeeds with workerRestartRequired=true; call python_reset before the next python_execute. " +
        "Preparing packages can access the network and may take substantially longer than a normal python_execute on first use.")]
    public async Task<CallToolResult> PreparePackages(
        [Description("Work session handle returned by work_create.")] string workId,
        [Description("Complete desired set of zero to 32 direct PyPI packages for this work session.")]
        [MaxLength(32)]
        PythonPackageRequirementInputDto[] packages,
        [Description("Maximum duration in seconds for resolution/download/install.")]
        [Range(1, 600)]
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        if (packages is null)
        {
            return McpToolResults.From(
                ToolEnvelope<PythonPackagesPrepareDto>.From(
                    LoomResult<PythonPackagesPrepareDto>.Failure(
                        LoomErrors.InvalidArgument(
                            "packages is required."))));
        }

        var mapped = new PythonPackageRequirement[packages.Length];
        for (var index = 0; index < packages.Length; index++)
        {
            var package = packages[index];
            if (package is null)
            {
                return McpToolResults.From(
                    ToolEnvelope<PythonPackagesPrepareDto>.From(
                        LoomResult<PythonPackagesPrepareDto>.Failure(
                            LoomErrors.InvalidArgument(
                                "packages cannot contain null entries."))));
            }

            mapped[index] = new PythonPackageRequirement(
                package.Name,
                package.Version);
        }

        var result = await _packages.PrepareAsync(
                new PythonPackagesPrepareRequest(
                    new WorkId(workId),
                    mapped,
                    TimeSpan.FromSeconds(timeoutSeconds)),
                cancellationToken)
            .ConfigureAwait(false);

        return McpToolResults.From(MapPackages(result));
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

    private static ToolEnvelope<PythonPackagesPrepareDto> MapPackages(
        LoomResult<PythonPackagesPrepareResult> result)
    {
        if (!result.IsSuccess)
        {
            return ToolEnvelope<PythonPackagesPrepareDto>.From(
                LoomResult<PythonPackagesPrepareDto>.Failure(
                    result.Error!));
        }

        var value = result.Value!;
        return ToolEnvelope<PythonPackagesPrepareDto>.From(
            LoomResult<PythonPackagesPrepareDto>.Success(
                new PythonPackagesPrepareDto(
                    value.EnvironmentId,
                    value.PythonVersion,
                    value.Packages
                        .Select(package =>
                            new PythonResolvedPackageDto(
                                package.Name,
                                package.Version))
                        .ToArray(),
                    value.Reused,
                    value.WorkerRestartRequired)));
    }

    private static CallToolResult BuildExecutionToolResult(
        LoomResult<PythonExecutionResult> result)
    {
        if (!result.IsSuccess)
        {
            return McpToolResults.From(
                ToolEnvelope<PythonExecutionDto>.From(
                    LoomResult<PythonExecutionDto>.Failure(
                        result.Error!)));
        }

        var value = result.Value!;
        var rawImageBytes = value.Outputs.Sum(output => output.Bytes.LongLength);
        var outputDtos = new PythonExecutionOutputDto[value.Outputs.Count];
        var imageBlocks = new ContentBlock[value.Outputs.Count];

        for (var index = 0; index < value.Outputs.Count; index++)
        {
            var output = value.Outputs[index];
            if (output.Kind != PythonExecutionOutputKind.Image)
            {
                return PostExecutionError(
                    "unsupported_python_output_kind",
                    "Python completed, but it produced an output kind that this MCP adapter does not support.",
                    new Dictionary<string, object?>
                    {
                        ["outputIndex"] = index,
                        ["outputKind"] = output.Kind.ToString(),
                        ["imageCount"] = value.Outputs.Count,
                        ["rawImageBytes"] = rawImageBytes
                    });
            }

            var mimeType = VisualImageFormat.DetectMimeType(output.Bytes);
            if (mimeType is null)
            {
                return PostExecutionError(
                    "unsupported_image_format",
                    "Python completed, but one image output is not a valid PNG, JPEG, or WebP container.",
                    new Dictionary<string, object?>
                    {
                        ["outputIndex"] = index,
                        ["imageCount"] = value.Outputs.Count,
                        ["rawImageBytes"] = rawImageBytes
                    });
            }

            outputDtos[index] = new PythonExecutionOutputDto(
                "image",
                mimeType,
                output.Bytes.LongLength);
            imageBlocks[index] = ImageContentBlock.FromBytes(
                output.Bytes,
                mimeType);
        }

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
                    value.Exception.Traceback),
            outputDtos);

        var envelope = ToolEnvelope<PythonExecutionDto>.From(
            LoomResult<PythonExecutionDto>.Success(dto));
        var candidate = McpToolResults.From(envelope, imageBlocks);
        var serializedBytes =
            McpPayloadLimits.MeasureSerializedCallToolResultBytes(candidate);

        if (serializedBytes > McpPayloadLimits.MaxCallToolResultBytes)
        {
            return PostExecutionError(
                "python_result_too_large",
                "Python completed, but the final MCP result exceeds the safe 9 MiB response budget. Reduce maxOutputChars or the size/number of displayed images before the next execution.",
                new Dictionary<string, object?>
                {
                    ["serializedCallToolResultBytes"] = serializedBytes,
                    ["maxCallToolResultBytes"] =
                        McpPayloadLimits.MaxCallToolResultBytes,
                    ["imageCount"] = value.Outputs.Count,
                    ["rawImageBytes"] = rawImageBytes
                });
        }

        return candidate;
    }

    private static CallToolResult PostExecutionError(
        string reason,
        string message,
        Dictionary<string, object?> details)
    {
        details["reason"] = reason;
        details["executionCompleted"] = true;

        return McpToolResults.From(
            ToolEnvelope<PythonExecutionDto>.From(
                LoomResult<PythonExecutionDto>.Failure(
                    new LoomError(
                        "unsupported",
                        message,
                        false,
                        details))));
    }
}
