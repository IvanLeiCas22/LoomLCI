using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;

namespace LoomLCI.Windows.Python;

internal sealed record PythonPackageEnvironmentMarker(
    int SchemaVersion,
    string EnvironmentId,
    string PythonVersion,
    string PythonArchitecture,
    string RuntimeSha256,
    string LockSha256,
    IReadOnlyList<PythonResolvedPackage> Packages);

public sealed class WindowsPythonPackageProvider : IPythonPackageProvider
{
    internal const int PackageStoreSchemaVersion = 2;
    internal const int MaxUvOutputChars = 262_144;
    internal const long DefaultEnvironmentBudgetBytes =
        2L * 1024 * 1024 * 1024;
    internal const long DefaultCacheBudgetBytes =
        1L * 1024 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        EnvironmentGates = new(StringComparer.Ordinal);

    private static readonly Regex LockedPackageLine = new(
        @"^([A-Za-z0-9][A-Za-z0-9._-]*)==([^\s\\]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProcessProvider _processProvider;
    private readonly IPythonRuntimeProvisioner _runtimeProvisioner;
    private readonly IPythonPackageManagerProvisioner _packageManagerProvisioner;
    private readonly string _loomRootDirectory;
    private readonly long _environmentBudgetBytes;
    private readonly long _cacheBudgetBytes;

    public WindowsPythonPackageProvider(
        IProcessProvider processProvider)
        : this(
            processProvider,
            new PythonRuntimeProvisioner(),
            new PythonPackageManagerProvisioner(),
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LoomLCI"))
    {
    }

    internal WindowsPythonPackageProvider(
        IProcessProvider processProvider,
        IPythonRuntimeProvisioner runtimeProvisioner,
        IPythonPackageManagerProvisioner packageManagerProvisioner,
        string loomRootDirectory,
        long environmentBudgetBytes = DefaultEnvironmentBudgetBytes,
        long cacheBudgetBytes = DefaultCacheBudgetBytes)
    {
        if (environmentBudgetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(environmentBudgetBytes));
        }

        if (cacheBudgetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheBudgetBytes));
        }

        _processProvider = processProvider;
        _runtimeProvisioner = runtimeProvisioner;
        _packageManagerProvisioner = packageManagerProvisioner;
        _loomRootDirectory = loomRootDirectory;
        _environmentBudgetBytes = environmentBudgetBytes;
        _cacheBudgetBytes = cacheBudgetBytes;
    }

    public async Task<LoomResult<PythonPackagesProviderResult>> PrepareAsync(
        PythonPackagesPrepareSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        try
        {
            var runtime = await _runtimeProvisioner
                .EnsureAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!runtime.IsSuccess)
            {
                return LoomResult<PythonPackagesProviderResult>.Failure(
                    runtime.Error!);
            }

            var runtimeInstallation = runtime.Value!;

            if (spec.Packages.Count == 0)
            {
                return LoomResult<PythonPackagesProviderResult>.Success(
                    new PythonPackagesProviderResult(
                        Environment: null,
                        runtimeInstallation.Manifest.Version,
                        Reused: true));
            }

            var packageManager = await _packageManagerProvisioner
                .EnsureAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!packageManager.IsSuccess)
            {
                return LoomResult<PythonPackagesProviderResult>.Failure(
                    packageManager.Error!);
            }

            return await PrepareEnvironmentAsync(
                    runtimeInstallation,
                    packageManager.Value!,
                    spec.Packages,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<PythonPackagesProviderResult>.Failure(
                LoomErrors.AccessDenied(
                    $"Could not prepare Python package environment: {ex.Message}"));
        }
        catch (Exception ex) when (
            ex is IOException or
            InvalidDataException or
            JsonException or
            CryptographicException or
            InvalidOperationException)
        {
            return LoomResult<PythonPackagesProviderResult>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not prepare Python package environment: {ex.Message}"));
        }
    }

    public async Task<LoomResult<Unit>> PruneAsync(
        IReadOnlySet<string> protectedEnvironmentIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protectedEnvironmentIds);

        try
        {
            var runtime = await _runtimeProvisioner
                .EnsureAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!runtime.IsSuccess)
            {
                return LoomResult<Unit>.Failure(
                    runtime.Error!);
            }

            var storeRoot = StoreRoot(runtime.Value!.Manifest);
            PruneEnvironments(
                storeRoot,
                protectedEnvironmentIds,
                cancellationToken);
            PruneUvCache(
                storeRoot,
                cancellationToken);

            return LoomResult<Unit>.Success(Unit.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<Unit>.Failure(
                LoomErrors.AccessDenied(
                    $"Could not prune Python package cache: {ex.Message}"));
        }
        catch (Exception ex) when (
            ex is IOException or
            InvalidDataException or
            InvalidOperationException)
        {
            return LoomResult<Unit>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not prune Python package cache: {ex.Message}"));
        }
    }

    private async Task<LoomResult<PythonPackagesProviderResult>>
        PrepareEnvironmentAsync(
            PythonRuntimeInstallation runtime,
            PythonPackageManagerInstallation packageManager,
            IReadOnlyList<PythonPackageRequirement> packages,
            CancellationToken cancellationToken)
    {
        var storeRoot = StoreRoot(runtime.Manifest);
        var environmentsRoot = Path.Combine(storeRoot, "envs");
        var stagingRoot = Path.Combine(storeRoot, "staging");
        var cacheRoot = Path.Combine(storeRoot, "cache");

        Directory.CreateDirectory(environmentsRoot);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(cacheRoot);

        var resolutionDirectory = Path.Combine(
            stagingRoot,
            $"resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resolutionDirectory);

        try
        {
            var inputPath = Path.Combine(
                resolutionDirectory,
                "requirements.in");
            var lockPath = Path.Combine(
                resolutionDirectory,
                "requirements.lock.txt");

            await File.WriteAllTextAsync(
                    inputPath,
                    BuildRequirements(packages),
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true),
                    cancellationToken)
                .ConfigureAwait(false);

            var compile = await RunUvAsync(
                    packageManager.ExecutablePath,
                    BuildCompileArguments(
                        inputPath,
                        lockPath,
                        runtime),
                    resolutionDirectory,
                    cacheRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!compile.IsSuccess)
            {
                return LoomResult<PythonPackagesProviderResult>.Failure(
                    compile.Error!);
            }

            if (!File.Exists(lockPath))
            {
                return LoomResult<PythonPackagesProviderResult>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Python package resolver completed without producing a lock file."));
            }

            var lockBytes = await File.ReadAllBytesAsync(
                    lockPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var lockText = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(lockBytes);
            var resolvedPackages = ParseLockedPackages(lockText);
            if (resolvedPackages.Count == 0)
            {
                return LoomResult<PythonPackagesProviderResult>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Python package lock did not contain any resolved packages."));
            }

            var lockSha256 = Convert.ToHexStringLower(
                SHA256.HashData(lockBytes));
            var environmentId = ComputeEnvironmentId(
                runtime.Manifest,
                lockBytes);
            var environmentDirectory = Path.Combine(
                environmentsRoot,
                environmentId);
            var gate = EnvironmentGates.GetOrAdd(
                environmentDirectory,
                _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var existing = ReadExistingEnvironment(
                    environmentDirectory,
                    runtime.Manifest,
                    environmentId,
                    lockSha256);
                if (existing is not null)
                {
                    TouchEnvironment(
                        storeRoot,
                        environmentId);

                    return LoomResult<PythonPackagesProviderResult>.Success(
                        new PythonPackagesProviderResult(
                            existing,
                            runtime.Manifest.Version,
                            Reused: true));
                }

                if (Directory.Exists(environmentDirectory) &&
                    !TryRemoveInvalidEnvironment(environmentDirectory))
                {
                    return LoomResult<PythonPackagesProviderResult>.Failure(
                        LoomErrors.ExecutionFailed(
                            $"Python package environment '{environmentId}' exists but is invalid and could not be replaced."));
                }

                var installStaging = Path.Combine(
                    stagingRoot,
                    $"install-{Guid.NewGuid():N}");
                Directory.CreateDirectory(installStaging);

                try
                {
                    var sitePath = Path.Combine(
                        installStaging,
                        "site");
                    Directory.CreateDirectory(sitePath);

                    var stagedLockPath = Path.Combine(
                        installStaging,
                        "requirements.lock.txt");
                    await File.WriteAllBytesAsync(
                            stagedLockPath,
                            lockBytes,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var sync = await RunUvAsync(
                            packageManager.ExecutablePath,
                            BuildSyncArguments(
                                stagedLockPath,
                                sitePath,
                                runtime),
                            installStaging,
                            cacheRoot,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!sync.IsSuccess)
                    {
                        return LoomResult<PythonPackagesProviderResult>.Failure(
                            sync.Error!);
                    }

                    if (PublishesReservedLoomNamespace(sitePath))
                    {
                        return LoomResult<PythonPackagesProviderResult>.Failure(
                            LoomErrors.Unsupported(
                                "Python package environment publishes the reserved top-level module 'loom'."));
                    }

                    var marker = new PythonPackageEnvironmentMarker(
                        PackageStoreSchemaVersion,
                        environmentId,
                        runtime.Manifest.Version,
                        runtime.Manifest.Architecture,
                        runtime.Manifest.Sha256,
                        lockSha256,
                        resolvedPackages);

                    await File.WriteAllBytesAsync(
                            MarkerPath(installStaging),
                            JsonSerializer.SerializeToUtf8Bytes(
                                marker,
                                new JsonSerializerOptions
                                {
                                    WriteIndented = true
                                }),
                            cancellationToken)
                        .ConfigureAwait(false);

                    Directory.Move(
                        installStaging,
                        environmentDirectory);

                    TouchEnvironment(
                        storeRoot,
                        environmentId);

                    var prepared = new PythonPackageEnvironment(
                        environmentId,
                        runtime.Manifest.Version,
                        Path.Combine(
                            environmentDirectory,
                            "site"),
                        resolvedPackages);

                    return LoomResult<PythonPackagesProviderResult>.Success(
                        new PythonPackagesProviderResult(
                            prepared,
                            runtime.Manifest.Version,
                            Reused: false));
                }
                finally
                {
                    TryDeleteDirectory(installStaging);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            TryDeleteDirectory(resolutionDirectory);
        }
    }

    private async Task<LoomResult<Unit>> RunUvAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var started = await _processProvider.StartAsync(
                new ProcessLaunchSpec(
                    executable,
                    arguments,
                    workingDirectory,
                    new Dictionary<string, string?>
                    {
                        ["UV_CACHE_DIR"] = cacheDirectory,
                        ["UV_NO_CONFIG"] = "1",
                        ["UV_PYTHON_DOWNLOADS"] = "never"
                    },
                    ProcessIoMode.Pipes,
                    null,
                    null),
                cancellationToken)
            .ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return LoomResult<Unit>.Failure(started.Error!);
        }

        var process = started.Value!;
        var diagnosticHandle = new ProcessHandle(
            $"proc_python_packages_{process.ProcessId}");

        try
        {
            await process.WaitForExitAndOutputAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            var snapshot = process.Snapshot(diagnosticHandle);
            var output = process.Read(
                diagnosticHandle,
                stdoutCursor: 0,
                stderrCursor: 0,
                terminalCursor: 0,
                maxChars: MaxUvOutputChars);

            if (snapshot.ExitCode == 0)
            {
                return LoomResult<Unit>.Success(Unit.Value);
            }

            var stderr = JoinChunks(output.Stderr);
            var stdout = JoinChunks(output.Stdout);
            var diagnostics = string.Join(
                " | ",
                new[] { stderr, stdout }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (diagnostics.Length > 16_384)
            {
                diagnostics = diagnostics[..16_384];
            }

            return LoomResult<Unit>.Failure(
                LoomErrors.ExecutionFailed(
                    string.IsNullOrWhiteSpace(diagnostics)
                        ? $"Python package manager exited with code {snapshot.ExitCode}."
                        : $"Python package manager exited with code {snapshot.ExitCode}: {diagnostics}"));
        }
        catch (OperationCanceledException)
        {
            try
            {
                await process.TerminateAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> BuildCompileArguments(
        string inputPath,
        string lockPath,
        PythonRuntimeInstallation runtime)
        =>
        [
            "pip",
            "compile",
            inputPath,
            "--output-file",
            lockPath,
            "--generate-hashes",
            "--no-header",
            "--no-annotate",
            "--python",
            runtime.PythonExecutablePath,
            "--python-platform",
            UvPlatform(runtime.Manifest.Architecture),
            "--no-python-downloads",
            "--only-binary",
            ":all:",
            "--no-config",
            "--default-index",
            "https://pypi.org/simple",
            "--index-strategy",
            "first-index"
        ];

    private static IReadOnlyList<string> BuildSyncArguments(
        string lockPath,
        string sitePath,
        PythonRuntimeInstallation runtime)
        =>
        [
            "pip",
            "sync",
            lockPath,
            "--target",
            sitePath,
            "--python",
            runtime.PythonExecutablePath,
            "--no-python-downloads",
            "--only-binary",
            ":all:",
            "--require-hashes",
            "--no-config",
            "--default-index",
            "https://pypi.org/simple",
            "--index-strategy",
            "first-index"
        ];

    private static string BuildRequirements(
        IReadOnlyList<PythonPackageRequirement> packages)
    {
        var lines = packages
            .OrderBy(
                package => package.Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(package =>
                package.Version is null
                    ? package.Name
                    : $"{package.Name}=={package.Version}");

        return string.Join("\n", lines) + "\n";
    }

    private static IReadOnlyList<PythonResolvedPackage> ParseLockedPackages(
        string lockText)
    {
        var packages = new List<PythonResolvedPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(lockText);
        while (reader.ReadLine() is { } line)
        {
            var match = LockedPackageLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var version = match.Groups[2].Value;
            if (!seen.Add(name))
            {
                continue;
            }

            packages.Add(
                new PythonResolvedPackage(
                    name,
                    version));
        }

        packages.Sort(
            (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                left.Name,
                right.Name));

        return packages.AsReadOnly();
    }

    private static string ComputeEnvironmentId(
        PythonRuntimeManifest manifest,
        byte[] lockBytes)
    {
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        hash.AppendData(
            Encoding.UTF8.GetBytes(
                $"loom-python-packages-v{PackageStoreSchemaVersion}\n" +
                $"{manifest.Version}\n" +
                $"{manifest.Architecture}\n" +
                $"{manifest.Sha256}\n"));
        hash.AppendData(lockBytes);
        return Convert.ToHexStringLower(
            hash.GetHashAndReset());
    }

    private PythonPackageEnvironment? ReadExistingEnvironment(
        string environmentDirectory,
        PythonRuntimeManifest runtime,
        string environmentId,
        string lockSha256)
    {
        var sitePath = Path.Combine(
            environmentDirectory,
            "site");
        var lockPath = Path.Combine(
            environmentDirectory,
            "requirements.lock.txt");
        var markerPath = MarkerPath(environmentDirectory);

        if (!Directory.Exists(sitePath) ||
            !File.Exists(lockPath) ||
            !File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            var marker =
                JsonSerializer.Deserialize<PythonPackageEnvironmentMarker>(
                    File.ReadAllBytes(markerPath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (marker is null ||
                marker.SchemaVersion != PackageStoreSchemaVersion ||
                !string.Equals(
                    marker.EnvironmentId,
                    environmentId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    marker.PythonVersion,
                    runtime.Version,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    marker.PythonArchitecture,
                    runtime.Architecture,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    marker.RuntimeSha256,
                    runtime.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    marker.LockSha256,
                    lockSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                marker.Packages.Count == 0)
            {
                return null;
            }

            return new PythonPackageEnvironment(
                marker.EnvironmentId,
                marker.PythonVersion,
                sitePath,
                marker.Packages.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private void PruneEnvironments(
        string storeRoot,
        IReadOnlySet<string> protectedEnvironmentIds,
        CancellationToken cancellationToken)
    {
        var environmentsRoot = Path.Combine(
            storeRoot,
            "envs");
        if (!Directory.Exists(environmentsRoot))
        {
            return;
        }

        var candidates =
            new List<(string Id, string Path, long Size, DateTime AccessedAt)>();
        long totalBytes = 0;

        foreach (var directory in Directory.EnumerateDirectories(
                     environmentsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = Path.GetFileName(directory);
            var size = GetDirectorySize(
                directory,
                cancellationToken);
            totalBytes = checked(totalBytes + size);

            var accessPath = AccessPath(
                storeRoot,
                id);
            var accessedAt = File.Exists(accessPath)
                ? File.GetLastWriteTimeUtc(accessPath)
                : Directory.GetLastWriteTimeUtc(directory);

            candidates.Add(
                (id, directory, size, accessedAt));
        }

        if (totalBytes <= _environmentBudgetBytes)
        {
            CleanupRetiredDirectories(
                storeRoot,
                cancellationToken);
            return;
        }

        foreach (var candidate in candidates
                     .Where(candidate =>
                         !protectedEnvironmentIds.Contains(
                             candidate.Id))
                     .OrderBy(candidate => candidate.AccessedAt)
                     .ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (totalBytes <= _environmentBudgetBytes)
            {
                break;
            }

            if (TryRetireEnvironment(
                    storeRoot,
                    candidate.Path,
                    candidate.Id))
            {
                totalBytes -= candidate.Size;
                TryDeleteFile(
                    AccessPath(
                        storeRoot,
                        candidate.Id));
            }
        }

        CleanupRetiredDirectories(
            storeRoot,
            cancellationToken);
    }

    private void PruneUvCache(
        string storeRoot,
        CancellationToken cancellationToken)
    {
        var cacheRoot = Path.Combine(
            storeRoot,
            "cache");
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var size = GetDirectorySize(
            cacheRoot,
            cancellationToken);
        if (size <= _cacheBudgetBytes)
        {
            return;
        }

        var retiredCache = Path.Combine(
            storeRoot,
            $"cache.gc-{Guid.NewGuid():N}");

        Directory.Move(
            cacheRoot,
            retiredCache);
        Directory.CreateDirectory(cacheRoot);
        TryDeleteDirectory(retiredCache);
    }

    private static void TouchEnvironment(
        string storeRoot,
        string environmentId)
    {
        var accessRoot = Path.Combine(
            storeRoot,
            "access");
        Directory.CreateDirectory(accessRoot);

        var path = AccessPath(
            storeRoot,
            environmentId);

        using var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read);
        stream.SetLength(0);
        File.SetLastWriteTimeUtc(
            path,
            DateTime.UtcNow);
    }

    private static string AccessPath(
        string storeRoot,
        string environmentId)
        => Path.Combine(
            storeRoot,
            "access",
            $"{environmentId}.touch");

    private static long GetDirectorySize(
        string directory,
        CancellationToken cancellationToken)
    {
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                total = checked(
                    total + new FileInfo(file).Length);
            }
            catch (FileNotFoundException)
            {
            }
        }

        return total;
    }

    private static bool PublishesReservedLoomNamespace(
        string sitePath)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     sitePath,
                     "loom*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry) &&
                string.Equals(
                    name,
                    "loom",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(entry))
            {
                continue;
            }

            var extension = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.Equals(
                    stem,
                    "loom",
                    StringComparison.OrdinalIgnoreCase) &&
                extension is not null &&
                (extension.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".pyc", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".pyd", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryRetireEnvironment(
        string storeRoot,
        string environmentDirectory,
        string environmentId)
    {
        var retiredRoot = Path.Combine(
            storeRoot,
            "gc");
        Directory.CreateDirectory(retiredRoot);

        var retiredPath = Path.Combine(
            retiredRoot,
            $"{environmentId}-{Guid.NewGuid():N}");

        try
        {
            Directory.Move(
                environmentDirectory,
                retiredPath);
        }
        catch
        {
            return false;
        }

        TryDeleteDirectory(retiredPath);
        return true;
    }

    private static void CleanupRetiredDirectories(
        string storeRoot,
        CancellationToken cancellationToken)
    {
        var retiredRoot = Path.Combine(
            storeRoot,
            "gc");
        if (!Directory.Exists(retiredRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     retiredRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteDirectory(directory);
        }
    }

    private static bool TryRemoveInvalidEnvironment(
        string environmentDirectory)
    {
        var invalidPath =
            environmentDirectory +
            $".invalid-{Guid.NewGuid():N}";

        try
        {
            Directory.Move(
                environmentDirectory,
                invalidPath);
        }
        catch
        {
            return false;
        }

        TryDeleteDirectory(invalidPath);
        return true;
    }

    private string StoreRoot(
        PythonRuntimeManifest runtime)
        => Path.Combine(
            _loomRootDirectory,
            "packages",
            "python",
            $"{runtime.Version}-{runtime.Architecture}");

    private static string MarkerPath(
        string environmentDirectory)
        => Path.Combine(
            environmentDirectory,
            ".loom-packages.json");

    private static string UvPlatform(string architecture)
        => architecture.Equals(
            "amd64",
            StringComparison.OrdinalIgnoreCase)
            ? "x86_64-pc-windows-msvc"
            : throw new InvalidOperationException(
                $"Unsupported Python architecture '{architecture}' for package resolution.");

    private static string JoinChunks(
        OutputStreamReadResult? output)
        => output is null
            ? string.Empty
            : string.Concat(
                output.Chunks.Select(chunk => chunk.Text));

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(
                    path,
                    recursive: true);
            }
        }
        catch
        {
        }
    }
}
