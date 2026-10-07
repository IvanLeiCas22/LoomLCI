using System.Text.RegularExpressions;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Python;

public sealed class PythonPackageCapability
{
    public const int MaxDirectPackages = 32;
    public const int MaxPackageNameChars = 128;
    public const int MaxVersionChars = 128;
    public static readonly TimeSpan DefaultTimeout =
        TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxTimeout =
        TimeSpan.FromMinutes(10);

    private static readonly Regex PackageNamePattern = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9.!+_-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NormalizeSeparators = new(
        @"[-_.]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IPythonPackageProvider _provider;
    private readonly ResourceRegistry _resources;
    private readonly WorkSessionManager _sessions;
    private readonly InvocationRunner _invocations;
    private readonly LoomEventBus _events;
    private readonly SemaphoreSlim _prepareGate = new(1, 1);

    public PythonPackageCapability(
        IPythonPackageProvider provider,
        ResourceRegistry resources,
        WorkSessionManager sessions,
        InvocationRunner invocations,
        LoomEventBus events)
    {
        _provider = provider;
        _resources = resources;
        _sessions = sessions;
        _invocations = invocations;
        _events = events;
    }

    public Task<LoomResult<PythonPackagesPrepareResult>> PrepareAsync(
        PythonPackagesPrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkId.Value))
        {
            return Task.FromResult(
                LoomResult<PythonPackagesPrepareResult>.Failure(
                    LoomErrors.InvalidArgument(
                        "work_id is required.")));
        }

        if (request.Packages is null)
        {
            return Task.FromResult(
                LoomResult<PythonPackagesPrepareResult>.Failure(
                    LoomErrors.InvalidArgument(
                        "packages is required.")));
        }

        if (request.Packages.Count > MaxDirectPackages)
        {
            return Task.FromResult(
                LoomResult<PythonPackagesPrepareResult>.Failure(
                    LoomErrors.InvalidArgument(
                        $"packages must contain no more than {MaxDirectPackages} entries.")));
        }

        if (request.Timeout <= TimeSpan.Zero ||
            request.Timeout > MaxTimeout)
        {
            return Task.FromResult(
                LoomResult<PythonPackagesPrepareResult>.Failure(
                    LoomErrors.InvalidArgument(
                        $"Python package timeout must be greater than zero and no more than {MaxTimeout.TotalSeconds:0} seconds.")));
        }

        var normalized = Normalize(request.Packages);
        if (!normalized.IsSuccess)
        {
            return Task.FromResult(
                LoomResult<PythonPackagesPrepareResult>.Failure(
                    normalized.Error!));
        }

        return _invocations.RunAsync(
            "python.packages.prepare",
            request.WorkId,
            async (context, token) =>
            {
                var session = context.WorkSession!;

                await _prepareGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var prepared = await _provider.PrepareAsync(
                            new PythonPackagesPrepareSpec(
                                normalized.Value!),
                            token)
                        .ConfigureAwait(false);
                    if (!prepared.IsSuccess)
                    {
                        return LoomResult<PythonPackagesPrepareResult>.Failure(
                            prepared.Error!);
                    }

                    var providerResult = prepared.Value!;
                    var environment = providerResult.Environment;

                    var handles = _resources.GetActiveOwnedHandles(
                        PythonCapability.ResourceKind,
                        session.Id);
                    if (handles.Count > 1)
                    {
                        return LoomResult<PythonPackagesPrepareResult>.Failure(
                            LoomErrors.Internal(
                                $"Work session '{session.Id}' has {handles.Count} active Python workers; expected at most one."));
                    }

                    string? activeEnvironmentId = null;
                    if (handles.Count == 1)
                    {
                        var resolved =
                            _resources.Resolve<IPythonWorkerResource>(
                                handles[0],
                                PythonCapability.ResourceKind);
                        if (resolved.IsSuccess)
                        {
                            activeEnvironmentId =
                                resolved.Value!.Resource.PackageEnvironmentId;
                        }
                    }

                    var set = session.SetPythonPackageEnvironment(
                        environment);
                    if (!set.IsSuccess)
                    {
                        return LoomResult<PythonPackagesPrepareResult>.Failure(
                            set.Error!);
                    }

                    var desiredEnvironmentId =
                        environment?.EnvironmentId;
                    var restartRequired =
                        handles.Count == 1 &&
                        !string.Equals(
                            activeEnvironmentId,
                            desiredEnvironmentId,
                            StringComparison.Ordinal);

                    _events.Publish(
                        "PythonPackagesPrepared",
                        "python",
                        session.Id,
                        context.Id,
                        payload: new Dictionary<string, object?>
                        {
                            ["environmentId"] =
                                desiredEnvironmentId,
                            ["packageCount"] =
                                environment?.Packages.Count ?? 0,
                            ["reused"] =
                                providerResult.Reused,
                            ["workerRestartRequired"] =
                                restartRequired
                        });

                    var protectedEnvironmentIds =
                        GetProtectedEnvironmentIds();
                    var pruned = await _provider.PruneAsync(
                            protectedEnvironmentIds,
                            token)
                        .ConfigureAwait(false);
                    if (!pruned.IsSuccess)
                    {
                        _events.Publish(
                            "PythonPackageCachePruneFailed",
                            "python",
                            session.Id,
                            context.Id,
                            payload: new Dictionary<string, object?>
                            {
                                ["errorCode"] =
                                    pruned.Error?.Code,
                                ["message"] =
                                    pruned.Error?.Message
                            });
                    }

                    return LoomResult<PythonPackagesPrepareResult>.Success(
                        new PythonPackagesPrepareResult(
                            desiredEnvironmentId,
                            providerResult.PythonVersion,
                            environment?.Packages ??
                                Array.Empty<PythonResolvedPackage>(),
                            providerResult.Reused,
                            restartRequired));
                }
                finally
                {
                    _prepareGate.Release();
                }
            },
            cancellationToken,
            request.Timeout);
    }

    private IReadOnlySet<string> GetProtectedEnvironmentIds()
    {
        var environmentIds = new HashSet<string>(
            _sessions.GetActivePythonPackageEnvironmentIds(),
            StringComparer.Ordinal);

        foreach (var handle in _resources.GetActiveHandles(
                     PythonCapability.ResourceKind))
        {
            var resolved = _resources.Resolve<IPythonWorkerResource>(
                handle,
                PythonCapability.ResourceKind);
            if (resolved.IsSuccess &&
                resolved.Value!.Resource.PackageEnvironmentId is { } environmentId)
            {
                environmentIds.Add(environmentId);
            }
        }

        return environmentIds;
    }

    private static LoomResult<IReadOnlyList<PythonPackageRequirement>>
        Normalize(
            IReadOnlyList<PythonPackageRequirement> packages)
    {
        if (packages.Count == 0)
        {
            return LoomResult<IReadOnlyList<PythonPackageRequirement>>
                .Success(
                    Array.Empty<PythonPackageRequirement>());
        }

        var normalized =
            new List<PythonPackageRequirement>(packages.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            if (package is null)
            {
                return LoomResult<IReadOnlyList<PythonPackageRequirement>>
                    .Failure(
                        LoomErrors.InvalidArgument(
                            "packages cannot contain null entries."));
            }

            var name = package.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                name.Length > MaxPackageNameChars ||
                !PackageNamePattern.IsMatch(name))
            {
                return LoomResult<IReadOnlyList<PythonPackageRequirement>>
                    .Failure(
                        LoomErrors.InvalidArgument(
                            $"Invalid Python package name '{package.Name}'."));
            }

            var normalizedName = NormalizeSeparators.Replace(
                    name.ToLowerInvariant(),
                    "-");

            if (!seen.Add(normalizedName))
            {
                return LoomResult<IReadOnlyList<PythonPackageRequirement>>
                    .Failure(
                        LoomErrors.InvalidArgument(
                            $"Python package '{normalizedName}' appears more than once."));
            }

            string? version = null;
            if (!string.IsNullOrWhiteSpace(package.Version))
            {
                version = package.Version.Trim();
                if (version.Length > MaxVersionChars ||
                    !VersionPattern.IsMatch(version))
                {
                    return LoomResult<IReadOnlyList<PythonPackageRequirement>>
                        .Failure(
                            LoomErrors.InvalidArgument(
                                $"Invalid exact version '{package.Version}' for Python package '{normalizedName}'."));
                }
            }

            normalized.Add(
                new PythonPackageRequirement(
                    normalizedName,
                    version));
        }

        normalized.Sort(
            (left, right) =>
                StringComparer.Ordinal.Compare(
                    left.Name,
                    right.Name));

        return LoomResult<IReadOnlyList<PythonPackageRequirement>>.Success(
            normalized.AsReadOnly());
    }
}
