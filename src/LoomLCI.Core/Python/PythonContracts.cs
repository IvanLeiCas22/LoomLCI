using System.Text.Json;

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

public sealed record PythonPackageRequirement(
    string Name,
    string? Version = null);

public sealed record PythonResolvedPackage(
    string Name,
    string Version);

public sealed record PythonPackageEnvironment(
    string EnvironmentId,
    string PythonVersion,
    string SitePath,
    IReadOnlyList<PythonResolvedPackage> Packages);

public sealed record PythonPackagesPrepareRequest(
    WorkId WorkId,
    IReadOnlyList<PythonPackageRequirement> Packages,
    TimeSpan Timeout);

public sealed record PythonPackagesPrepareSpec(
    IReadOnlyList<PythonPackageRequirement> Packages);

public sealed record PythonPackagesProviderResult(
    PythonPackageEnvironment? Environment,
    string PythonVersion,
    bool Reused);

public sealed record PythonPackagesPrepareResult(
    string? EnvironmentId,
    string PythonVersion,
    IReadOnlyList<PythonResolvedPackage> Packages,
    bool Reused,
    bool WorkerRestartRequired);

public sealed record PythonWorkerStartSpec(
    WorkId WorkId,
    string? WorkingDirectory,
    PythonPackageEnvironment? PackageEnvironment = null);

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

public sealed record PythonBridgeCall(
    string Method,
    JsonElement Arguments);

public delegate Task<LoomResult<JsonElement>> PythonBridgeHandler(
    PythonBridgeCall call,
    CancellationToken cancellationToken);

public interface IPythonBridgeModule
{
    IReadOnlyList<string> Methods { get; }

    Task<LoomResult<JsonElement>> DispatchAsync(
        WorkId workId,
        PythonBridgeCall call,
        CancellationToken cancellationToken);
}

public interface IPythonBridgeDispatcher
{
    Task<LoomResult<JsonElement>> DispatchAsync(
        WorkId workId,
        PythonBridgeCall call,
        CancellationToken cancellationToken);
}

public interface IPythonWorkerResource : IAsyncDisposable
{
    bool IsHealthy { get; }

    string? PackageEnvironmentId { get; }

    Task<LoomResult<PythonExecutionResult>> ExecuteAsync(
        PythonWorkerExecuteSpec request,
        PythonBridgeHandler bridgeHandler,
        CancellationToken cancellationToken);
}

public interface IPythonRuntimeProvider
{
    Task<LoomResult<IPythonWorkerResource>> StartAsync(
        PythonWorkerStartSpec spec,
        CancellationToken cancellationToken);
}

public interface IPythonPackageProvider
{
    Task<LoomResult<PythonPackagesProviderResult>> PrepareAsync(
        PythonPackagesPrepareSpec spec,
        CancellationToken cancellationToken);

    Task<LoomResult<Unit>> PruneAsync(
        IReadOnlySet<string> protectedEnvironmentIds,
        CancellationToken cancellationToken);
}
