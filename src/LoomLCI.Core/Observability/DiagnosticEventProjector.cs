namespace LoomLCI.Core.Observability;

/// <summary>Fixed-schema allowlist. Arbitrary payload text is deliberately discarded.</summary>
public static class DiagnosticEventProjector
{
    public static DiagnosticRecord? Project(LoomEvent evt)
    {
        var supported = evt.Kind is
            "InvocationStarted" or "InvocationCompleted" or
            "WorkSessionCreated" or "WorkSessionClosing" or
            "WorkSessionClosed" or "WorkSessionExpired" or "WorkSessionCleanupFailed" or
            "WorkSessionExpiring" or "ResourceCreated" or "ResourceClosed" or
            "ResourceExpired" or "ProcessTerminated" or "FilesystemPatched" or
            "DirectoryCreated" or "DirectoryDeleted" or "WorkPlanUpdated" or
            "PythonPackagesPrepared" or "PythonPackageCachePruneFailed" or "PythonExecutionException";
        if (!supported)
            return null;

        string? operation = null;
        string? outcome = null;
        string? code = null;
        long? count = null;
        long? durationMs = null;
        var payload = evt.Payload;
        if (evt.Kind is "InvocationStarted" or "InvocationCompleted" && payload is not null &&
            payload.TryGetValue("operation", out var operationValue))
            operation = SafeSymbol(operationValue);
        if (evt.Kind == "InvocationCompleted" && payload is not null)
        {
            if (payload.TryGetValue("success", out var success) && success is bool state)
                outcome = state ? "success" : "failure";
            if (payload.TryGetValue("error", out var error))
                code = SafeSymbol(error);
        }
        if (evt.Kind == "InvocationCompleted" && payload is not null &&
            payload.TryGetValue("duration_ms", out var duration) &&
            duration is long ms && ms is >= 0 and <= 86_400_000)
            durationMs = ms;
        if (evt.Kind == "WorkSessionCleanupFailed" && payload is not null &&
            payload.TryGetValue("code", out var cleanupCode))
            code = SafeSymbol(cleanupCode);
        if (evt.Kind == "FilesystemPatched" && payload is not null &&
            payload.TryGetValue("changes", out var changes) && changes is int changed)
            count = changed;

        return new DiagnosticRecord(evt.Timestamp, "host", evt.Kind,
            Operation: operation, Outcome: outcome, Code: code,
            Count: count, DurationMs: durationMs);
    }

    private static string? SafeSymbol(object? value)
    {
        if (value is not string text || text.Length is < 1 or > 64 ||
            text[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z') ||
            !text.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '_' or '.' or '-'))
            return null;
        return text;
    }
}
