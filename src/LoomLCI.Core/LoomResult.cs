namespace LoomLCI.Core;

public sealed record LoomError(
    string Code,
    string Message,
    bool? Retryable = null,
    IReadOnlyDictionary<string, object?>? Details = null);

public readonly record struct Unit
{
    public static Unit Value => new();
}

public sealed class LoomResult<T>
{
    private LoomResult(T? value, LoomError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;
    public T? Value { get; }
    public LoomError? Error { get; }

    public static LoomResult<T> Success(T value) => new(value, null);
    public static LoomResult<T> Failure(LoomError error) => new(default, error);
}

public static class LoomErrors
{
    public static LoomError InvalidArgument(string message) => new("invalid_argument", message, false);
    public static LoomError NotFound(string message) => new("not_found", message, false);
    public static LoomError Conflict(
        string message,
        IReadOnlyDictionary<string, object?>? details = null)
        => new("conflict", message, false, details);
    public static LoomError AccessDenied(string message) => new("access_denied", message, false);
    public static LoomError Unsupported(string message) => new("unsupported", message, false);
    public static LoomError ResourceClosed(string handle) => new("resource_closed", $"Resource '{handle}' is closed.", false);
    public static LoomError ResourceExpired(string handle) => new("resource_expired", $"Resource '{handle}' has expired.", false);
    public static LoomError ResourceTypeMismatch(string handle, string expected)
        => new("resource_type_mismatch", $"Resource '{handle}' is not a '{expected}'.", false);
    public static LoomError Busy(string message) => new("busy", message, true);
    public static LoomError Cancelled() => new("cancelled", "The operation was cancelled.", true);
    public static LoomError DeadlineExceeded() => new("deadline_exceeded", "The operation exceeded its deadline.", true);
    public static LoomError ExecutionFailed(string message) => new("execution_failed", message, false);
    public static LoomError Internal(string message) => new("internal", message, false);
}
