#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LoomLCI.Core;
using LoomLCI.Core.Python;

namespace LoomLCI.Windows.Python;

internal sealed class PythonWorkerProtocolException(string message, Exception? inner = null)
    : Exception(message, inner);

internal sealed record PythonWorkerHello(
    int ProtocolVersion,
    int ProcessId,
    string PythonVersion);

internal abstract record PythonWorkerExecutionMessage;

internal sealed record PythonWorkerResultMessage(
    PythonExecutionResult Result) : PythonWorkerExecutionMessage;

internal sealed record PythonWorkerBridgeCallMessage(
    string CallId,
    PythonBridgeCall Call) : PythonWorkerExecutionMessage;

internal static class PythonWorkerProtocol
{
    public const int ProtocolVersion = 3;
    public const int MaxRequestFrameBytes = 2 * 1024 * 1024;
    public const int MaxResponseFrameBytes = 40 * 1024 * 1024;
    public const int MaxBridgeCallFrameBytes = PythonBridgeLimits.MaxCallFrameBytes;
    public const int MaxBridgeResultFrameBytes = PythonBridgeLimits.MaxResultFrameBytes;
    public const int MaxHelloFrameBytes = 16 * 1024;
    public const int MaxCodeUtf8Bytes = PythonCapability.MaxCodeUtf8Bytes;
    public const int MaxRequestIdChars = 128;
    public const int MaxCallIdChars = 128;
    public const int MaxBridgeMethodChars = PythonBridgeLimits.MaxMethodChars;
    public const int MaxExceptionMessageChars = 16 * 1024;
    public const int MaxTracebackChars = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    public static string CreatePipeName()
        => $"loom-python-{Guid.NewGuid():N}";

    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        ValidatePipeName(pipeName);

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
            ?? throw new InvalidOperationException("Could not determine the current Windows user SID.");

        var network = new SecurityIdentifier(
            WellKnownSidType.NetworkSid,
            domainSid: null);

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(
            new PipeAccessRule(
                network,
                PipeAccessRights.FullControl,
                AccessControlType.Deny));
        security.AddAccessRule(
            new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }

    public static async Task<PythonWorkerHello> ReadHelloAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await ReadFrameAsync(
                stream,
                MaxHelloFrameBytes,
                cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        RequireObject(root);

        if (ReadRequiredString(root, "type") != "hello")
        {
            throw new PythonWorkerProtocolException(
                "Expected Python worker hello frame.");
        }

        var version = ReadRequiredInt32(root, "protocolVersion");
        if (version != ProtocolVersion)
        {
            throw new PythonWorkerProtocolException(
                $"Python worker protocol version {version} is incompatible with {ProtocolVersion}.");
        }

        var processId = ReadRequiredInt32(root, "pid");
        if (processId <= 0)
        {
            throw new PythonWorkerProtocolException(
                "Python worker hello contained an invalid process id.");
        }

        var pythonVersion = ReadRequiredString(root, "pythonVersion");
        if (string.IsNullOrWhiteSpace(pythonVersion) || pythonVersion.Length > 64)
        {
            throw new PythonWorkerProtocolException(
                "Python worker hello contained an invalid Python version.");
        }

        return new PythonWorkerHello(version, processId, pythonVersion);
    }

    public static async Task WriteExecuteRequestAsync(
        Stream stream,
        string requestId,
        PythonWorkerExecuteSpec request,
        CancellationToken cancellationToken)
    {
        ValidateRequestId(requestId);

        int codeBytes;
        try
        {
            codeBytes = StrictUtf8.GetByteCount(request.Code);
        }
        catch (EncoderFallbackException ex)
        {
            throw new PythonWorkerProtocolException(
                "Python code is not valid Unicode.",
                ex);
        }

        if (codeBytes > MaxCodeUtf8Bytes)
        {
            throw new PythonWorkerProtocolException(
                $"Python code exceeds {MaxCodeUtf8Bytes} UTF-8 bytes.");
        }

        if (request.MaxOutputChars is < 1 or > PythonCapability.MaxOutputChars)
        {
            throw new PythonWorkerProtocolException(
                $"maxOutputChars must be between 1 and {PythonCapability.MaxOutputChars}.");
        }

        var envelope = new ExecuteEnvelope(
            "execute",
            requestId,
            request.Code,
            request.MaxOutputChars);

        await WriteFrameAsync(
                stream,
                envelope,
                MaxRequestFrameBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<PythonExecutionResult> ReadExecutionResultAsync(
        Stream stream,
        string expectedRequestId,
        int maxOutputChars,
        CancellationToken cancellationToken)
    {
        ValidateRequestId(expectedRequestId);

        using var document = await ReadFrameAsync(
                stream,
                MaxResponseFrameBytes,
                cancellationToken)
            .ConfigureAwait(false);

        return ParseExecutionResult(
            document.RootElement,
            expectedRequestId,
            maxOutputChars);
    }

    public static async Task<PythonWorkerExecutionMessage> ReadExecutionMessageAsync(
        Stream stream,
        string expectedRequestId,
        int maxOutputChars,
        CancellationToken cancellationToken)
    {
        ValidateRequestId(expectedRequestId);

        using var document = await ReadFrameAsync(
                stream,
                MaxResponseFrameBytes,
                cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        RequireObject(root);

        var type = ReadRequiredString(root, "type");
        return type switch
        {
            "result" => new PythonWorkerResultMessage(
                ParseExecutionResult(
                    root,
                    expectedRequestId,
                    maxOutputChars)),
            "bridge_call" => ParseBridgeCall(
                root,
                expectedRequestId),
            _ => throw new PythonWorkerProtocolException(
                $"Unsupported Python worker execution message type '{type}'.")
        };
    }

    public static async Task WriteBridgeResultAsync(
        Stream stream,
        string requestId,
        string callId,
        LoomResult<JsonElement> result,
        CancellationToken cancellationToken)
    {
        ValidateRequestId(requestId);
        ValidateCallId(callId);

        object envelope = result.IsSuccess
            ? new
            {
                type = "bridge_result",
                requestId,
                callId,
                ok = true,
                result = result.Value,
                error = (object?)null
            }
            : new
            {
                type = "bridge_result",
                requestId,
                callId,
                ok = false,
                result = (object?)null,
                error = new
                {
                    code = result.Error!.Code,
                    message = result.Error.Message,
                    retryable = result.Error.Retryable,
                    details = result.Error.Details
                }
            };

        var payload = SerializeFramePayload(envelope);
        if (payload.Length > MaxBridgeResultFrameBytes)
        {
            var oversizedPayloadBytes = payload.Length;
            envelope = new
            {
                type = "bridge_result",
                requestId,
                callId,
                ok = false,
                result = (object?)null,
                error = new
                {
                    code = "unsupported",
                    message = $"Python bridge result frame exceeds {MaxBridgeResultFrameBytes} bytes.",
                    retryable = false,
                    details = new
                    {
                        reason = "bridge_payload_too_large",
                        serialized_result_bytes = oversizedPayloadBytes,
                        max_bridge_result_bytes = MaxBridgeResultFrameBytes
                    }
                }
            };
            payload = SerializeFramePayload(envelope);
        }

        await WritePayloadAsync(
                stream,
                payload,
                MaxBridgeResultFrameBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static PythonExecutionResult ParseExecutionResult(
        JsonElement root,
        string expectedRequestId,
        int maxOutputChars)
    {
        RequireObject(root);

        if (ReadRequiredString(root, "type") != "result")
        {
            throw new PythonWorkerProtocolException(
                "Expected Python worker result frame.");
        }

        var requestId = ReadRequiredString(root, "requestId");
        if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new PythonWorkerProtocolException(
                "Python worker result requestId does not match the active request.");
        }

        var statusText = ReadRequiredString(root, "status");
        var status = statusText switch
        {
            "completed" => PythonExecutionStatus.Completed,
            "exception" => PythonExecutionStatus.Exception,
            _ => throw new PythonWorkerProtocolException(
                $"Unknown Python execution status '{statusText}'.")
        };

        var stdout = ReadRequiredString(root, "stdout");
        var stderr = ReadRequiredString(root, "stderr");
        if (CountUnicodeCodePoints(stdout) > maxOutputChars ||
            CountUnicodeCodePoints(stderr) > maxOutputChars)
        {
            throw new PythonWorkerProtocolException(
                "Python worker returned output beyond the negotiated character limit.");
        }

        var stdoutTruncated = ReadRequiredBoolean(root, "stdoutTruncated");
        var stderrTruncated = ReadRequiredBoolean(root, "stderrTruncated");

        PythonExceptionInfo? exception = null;
        if (root.TryGetProperty("exception", out var exceptionElement) &&
            exceptionElement.ValueKind != JsonValueKind.Null)
        {
            RequireObject(exceptionElement);

            var exceptionType = ReadRequiredString(exceptionElement, "type");
            var message = ReadRequiredString(exceptionElement, "message");
            var traceback = ReadRequiredString(exceptionElement, "traceback");

            if (CountUnicodeCodePoints(exceptionType) > 512 ||
                CountUnicodeCodePoints(message) > MaxExceptionMessageChars ||
                CountUnicodeCodePoints(traceback) > MaxTracebackChars)
            {
                throw new PythonWorkerProtocolException(
                    "Python worker returned oversized exception metadata.");
            }

            exception = new PythonExceptionInfo(
                exceptionType,
                message,
                traceback);
        }

        if (status == PythonExecutionStatus.Completed && exception is not null)
        {
            throw new PythonWorkerProtocolException(
                "Completed Python result must not contain exception metadata.");
        }

        if (status == PythonExecutionStatus.Exception && exception is null)
        {
            throw new PythonWorkerProtocolException(
                "Exceptional Python result must contain exception metadata.");
        }

        var outputs = ParseExecutionOutputs(root);

        return new PythonExecutionResult(
            status,
            stdout,
            stderr,
            stdoutTruncated,
            stderrTruncated,
            exception,
            outputs);
    }

    private static IReadOnlyList<PythonExecutionOutput> ParseExecutionOutputs(
        JsonElement root)
    {
        if (!root.TryGetProperty("outputs", out var outputsElement) ||
            outputsElement.ValueKind != JsonValueKind.Array)
        {
            throw new PythonWorkerProtocolException(
                "Python worker protocol field 'outputs' must be an array.");
        }

        if (outputsElement.GetArrayLength() > PythonOutputLimits.MaxOutputs)
        {
            throw new PythonWorkerProtocolException(
                $"Python worker returned more than {PythonOutputLimits.MaxOutputs} typed outputs.");
        }

        var outputs = new List<PythonExecutionOutput>(
            outputsElement.GetArrayLength());
        var totalBytes = 0;
        var maxBase64Chars = checked(
            4 * ((PythonOutputLimits.MaxImageBytes + 2) / 3));

        foreach (var outputElement in outputsElement.EnumerateArray())
        {
            RequireObject(outputElement);

            var kind = ReadRequiredString(outputElement, "kind");
            if (!string.Equals(kind, "image", StringComparison.Ordinal))
            {
                throw new PythonWorkerProtocolException(
                    $"Unsupported Python execution output kind '{kind}'.");
            }

            var encoded = ReadRequiredString(outputElement, "data");
            if (encoded.Length > maxBase64Chars)
            {
                throw new PythonWorkerProtocolException(
                    $"Python image output exceeds {PythonOutputLimits.MaxImageBytes} bytes.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                throw new PythonWorkerProtocolException(
                    "Python image output is not valid base64.",
                    ex);
            }

            if (bytes.Length == 0 ||
                bytes.Length > PythonOutputLimits.MaxImageBytes)
            {
                throw new PythonWorkerProtocolException(
                    $"Python image output must contain between 1 and {PythonOutputLimits.MaxImageBytes} bytes.");
            }

            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > PythonOutputLimits.MaxTotalOutputBytes)
            {
                throw new PythonWorkerProtocolException(
                    $"Python execution outputs exceed {PythonOutputLimits.MaxTotalOutputBytes} aggregate bytes.");
            }

            outputs.Add(
                new PythonExecutionOutput(
                    PythonExecutionOutputKind.Image,
                    bytes));
        }

        return outputs;
    }

    private static PythonWorkerBridgeCallMessage ParseBridgeCall(
        JsonElement root,
        string expectedRequestId)
    {
        var rawBytes = StrictUtf8.GetByteCount(root.GetRawText());
        if (rawBytes > MaxBridgeCallFrameBytes)
        {
            throw new PythonWorkerProtocolException(
                $"Python bridge call exceeds {MaxBridgeCallFrameBytes} bytes.");
        }

        var requestId = ReadRequiredString(root, "requestId");
        if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new PythonWorkerProtocolException(
                "Python bridge call requestId does not match the active request.");
        }

        var callId = ReadRequiredString(root, "callId");
        ValidateCallId(callId);

        var method = ReadRequiredString(root, "method");
        if (string.IsNullOrWhiteSpace(method) ||
            method.Length > MaxBridgeMethodChars)
        {
            throw new PythonWorkerProtocolException(
                "Python bridge method must be a non-empty bounded string.");
        }

        if (!root.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            throw new PythonWorkerProtocolException(
                "Python bridge arguments must be a JSON object.");
        }

        return new PythonWorkerBridgeCallMessage(
            callId,
            new PythonBridgeCall(
                method,
                arguments.Clone()));
    }

    internal static Task WriteRawFrameAsync(
        Stream stream,
        object value,
        int maxFrameBytes,
        CancellationToken cancellationToken = default)
        => WriteFrameAsync(stream, value, maxFrameBytes, cancellationToken);

    internal static Task<JsonDocument> ReadRawFrameAsync(
        Stream stream,
        int maxFrameBytes,
        CancellationToken cancellationToken = default)
        => ReadFrameAsync(stream, maxFrameBytes, cancellationToken);

    private static Task WriteFrameAsync(
        Stream stream,
        object value,
        int maxFrameBytes,
        CancellationToken cancellationToken)
        => WritePayloadAsync(
            stream,
            SerializeFramePayload(value),
            maxFrameBytes,
            cancellationToken);

    private static byte[] SerializeFramePayload(object value)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(
                value,
                JsonOptions);
        }
        catch (Exception ex) when (
            ex is JsonException or
            NotSupportedException)
        {
            throw new PythonWorkerProtocolException(
                "Could not serialize Python worker protocol frame.",
                ex);
        }
    }

    private static async Task WritePayloadAsync(
        Stream stream,
        byte[] payload,
        int maxFrameBytes,
        CancellationToken cancellationToken)
    {
        if (payload.Length < 2 || payload.Length > maxFrameBytes)
        {
            throw new PythonWorkerProtocolException(
                $"Python worker protocol frame size {payload.Length} is outside the allowed range.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            checked((uint)payload.Length));

        await stream.WriteAsync(
                header,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(
                payload,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadFrameAsync(
        Stream stream,
        int maxFrameBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(uint)];
        await ReadExactlyAsync(stream, header, cancellationToken)
            .ConfigureAwait(false);

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (payloadLength < 2 || payloadLength > maxFrameBytes)
        {
            throw new PythonWorkerProtocolException(
                $"Python worker protocol frame size {payloadLength} is outside the allowed range.");
        }

        var payload = new byte[checked((int)payloadLength)];
        await ReadExactlyAsync(stream, payload, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var text = StrictUtf8.GetString(payload);
            return JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
        }
        catch (Exception ex) when (
            ex is DecoderFallbackException or
            JsonException or
            ArgumentException)
        {
            throw new PythonWorkerProtocolException(
                "Python worker protocol frame is not valid strict UTF-8 JSON.",
                ex);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                    buffer[offset..],
                    cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new PythonWorkerProtocolException(
                    "Unexpected EOF while reading Python worker protocol frame.");
            }

            offset += read;
        }
    }

    private static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) ||
            pipeName.Length > 128 ||
            pipeName.Contains('\\') ||
            pipeName.Contains('/'))
        {
            throw new ArgumentException(
                "Pipe name must be a non-empty simple name of at most 128 characters.",
                nameof(pipeName));
        }
    }

    private static void ValidateRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            requestId.Length > MaxRequestIdChars)
        {
            throw new PythonWorkerProtocolException(
                "requestId must be a non-empty bounded string.");
        }
    }

    private static void ValidateCallId(string callId)
    {
        if (string.IsNullOrWhiteSpace(callId) ||
            callId.Length > MaxCallIdChars)
        {
            throw new PythonWorkerProtocolException(
                "callId must be a non-empty bounded string.");
        }
    }

    private static int CountUnicodeCodePoints(string value)
    {
        var count = 0;

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) &&
                index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1]))
            {
                index++;
            }

            count++;
        }

        return count;
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PythonWorkerProtocolException(
                "Python worker protocol frame root must be a JSON object.");
        }
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new PythonWorkerProtocolException(
                $"Python worker protocol field '{name}' must be a string.");
        }

        return value.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new PythonWorkerProtocolException(
                $"Python worker protocol field '{name}' must be an Int32.");
        }

        return result;
    }

    private static bool ReadRequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new PythonWorkerProtocolException(
                $"Python worker protocol field '{name}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private sealed record ExecuteEnvelope(
        string Type,
        string RequestId,
        string Code,
        int MaxOutputChars);
}
