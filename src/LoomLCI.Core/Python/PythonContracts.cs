namespace LoomLCI.Core.Python;

public enum PythonExecutionStatus
{
    Completed,
    Exception
}

public sealed record PythonExceptionInfo(
    string Type,
    string Message,
    string Traceback);

public sealed record PythonExecuteRequest(
    WorkId WorkId,
    string Code,
    TimeSpan Timeout,
    int MaxOutputChars = 65_536);

public sealed record PythonWorkerStartSpec(
    WorkId WorkId,
    string? WorkingDirectory);

public sealed record PythonWorkerExecuteSpec(
    string Code,
    int MaxOutputChars);

public sealed record PythonExecutionResult(
    PythonExecutionStatus Status,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    PythonExceptionInfo? Exception);

public interface IPythonWorkerResource : IAsyncDisposable
{
    bool IsHealthy { get; }

    Task<LoomResult<PythonExecutionResult>> ExecuteAsync(
        PythonWorkerExecuteSpec request,
        CancellationToken cancellationToken);
}

public interface IPythonRuntimeProvider
{
    Task<LoomResult<IPythonWorkerResource>> StartAsync(
        PythonWorkerStartSpec spec,
        CancellationToken cancellationToken);
}
