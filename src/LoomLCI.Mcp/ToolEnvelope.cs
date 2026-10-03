using LoomLCI.Core;

namespace LoomLCI.Mcp;

public sealed record ToolError(
    string Code,
    string Message,
    bool? Retryable,
    IReadOnlyDictionary<string, object?>? Details);

public sealed record ToolEnvelope<T>(
    bool Ok,
    T? Result,
    ToolError? Error)
{
    public static ToolEnvelope<T> From(LoomResult<T> result)
        => result.IsSuccess
            ? new ToolEnvelope<T>(true, result.Value, null)
            : new ToolEnvelope<T>(
                false,
                default,
                new ToolError(
                    result.Error!.Code,
                    result.Error.Message,
                    result.Error.Retryable,
                    result.Error.Details));
}
