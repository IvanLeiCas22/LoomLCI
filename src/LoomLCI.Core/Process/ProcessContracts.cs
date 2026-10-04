using LoomLCI.Core.Resources;

namespace LoomLCI.Core.Processes;

public enum ProcessIoMode
{
    Pipes,
    Terminal
}

public enum ManagedProcessState
{
    Starting,
    Running,
    Exited,
    Terminating,
    Terminated,
    FailedToStart
}

public sealed record ProcessStartRequest(
    string Executable,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    WorkId? WorkId = null,
    ResourceOwnership Ownership = ResourceOwnership.SessionOwned,
    ProcessIoMode IoMode = ProcessIoMode.Pipes,
    int? TerminalColumns = null,
    int? TerminalRows = null);

public sealed record ProcessLaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    ProcessIoMode IoMode,
    int? TerminalColumns,
    int? TerminalRows);

public sealed record ProcessStartResult(
    ProcessHandle Handle,
    int ProcessId,
    DateTimeOffset StartedAt,
    ManagedProcessState State,
    ProcessIoMode IoMode,
    TimeSpan PostExitRetention);

public sealed record ProcessStatusResult(
    ProcessHandle Handle,
    int ProcessId,
    ManagedProcessState State,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt,
    ProcessIoMode IoMode,
    DateTimeOffset? RetentionExpiresAt);

public sealed record OutputChunk(long Cursor, string Text);

public sealed record OutputStreamReadResult(
    long RequestedCursor,
    long EarliestAvailableCursor,
    long NextCursor,
    long RetainedUntilCursor,
    long ObservedUntilCursor,
    bool Truncated,
    bool RetentionLimitReached,
    IReadOnlyList<OutputChunk> Chunks);

public sealed record ProcessOutputReadResult(
    ProcessStatusResult Process,
    ProcessIoMode IoMode,
    OutputStreamReadResult? Stdout,
    OutputStreamReadResult? Stderr,
    OutputStreamReadResult? Terminal);

public interface IProcessResource : IAsyncDisposable
{
    int ProcessId { get; }
    DateTimeOffset StartedAt { get; }
    ProcessIoMode IoMode { get; }
    ProcessStatusResult Snapshot(ProcessHandle handle);
    ProcessOutputReadResult Read(
        ProcessHandle handle,
        long stdoutCursor,
        long stderrCursor,
        long terminalCursor,
        int maxChars);
    Task<LoomResult<Unit>> WriteAsync(string text, CancellationToken cancellationToken);
    Task<LoomResult<Unit>> ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
    Task<LoomResult<Unit>> TerminateAsync(CancellationToken cancellationToken);
}

public interface IProcessProvider
{
    Task<LoomResult<IProcessResource>> StartAsync(ProcessLaunchSpec spec, CancellationToken cancellationToken);
}
