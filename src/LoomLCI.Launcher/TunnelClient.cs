using System.Text.Json;

namespace LoomLCI.Launcher;

public sealed record TunnelRuntimeStatus(
    bool ProcessRunning,
    bool Healthy,
    bool Ready,
    string RuntimeState,
    string TunnelId,
    string Error)
{
    public bool IsReady =>
        ProcessRunning &&
        Healthy &&
        Ready &&
        string.Equals(RuntimeState, "ready", StringComparison.OrdinalIgnoreCase);

    public static TunnelRuntimeStatus Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new TunnelRuntimeStatus(
            GetBoolean(root, "process_running"),
            GetBoolean(root, "healthy"),
            GetBoolean(root, "ready"),
            GetString(root, "runtime_state"),
            GetString(root, "tunnel_id"),
            GetString(root, "error"));
    }

    private static bool GetBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

public sealed record TunnelStatusResult(
    bool CommandSucceeded,
    TunnelRuntimeStatus? Status,
    string ErrorText);

public sealed class TunnelClient
{
    public const string PinnedVersion = "0.0.14";
    public const string PinnedArchiveSha256 =
        "784ab8da7b5a88f0109f1fd8aaf0a1c86067430b896dddf307ef7e3cc49fa1a5";
    public const string PinnedArchiveUrl =
        "https://github.com/openai/tunnel-client/releases/download/v0.0.14/tunnel-client-v0.0.14-windows-amd64.zip";
    public const string PinnedExecutableSha256 =
        "fcc85a69ec0ad82518e4f8964f60c45e31787957782a0fc9c1b0c44e82d61b9b";

    private readonly ProcessRunner _runner;

    public TunnelClient(ProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<string> GetVersionAsync(
        AppPaths paths,
        CancellationToken cancellationToken)
    {
        EnsureBinary(paths);
        var result = await _runner.RunAsync(
            paths.TunnelClientPath,
            ["--version"],
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"tunnel-client --version falló: {PreferError(result)}");
        }

        return result.Stdout.Trim();
    }

    public async Task<TunnelStatusResult> StatusAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        EnsureBinary(paths);
        var result = await _runner.RunAsync(
            paths.TunnelClientPath,
            ["runtimes", "status", config.Alias, "--json"],
            BuildEnvironment(paths, config),
            TimeSpan.FromSeconds(20),
            cancellationToken);

        if (!result.Success)
        {
            return new TunnelStatusResult(
                false,
                null,
                PreferError(result));
        }

        try
        {
            return new TunnelStatusResult(
                true,
                TunnelRuntimeStatus.Parse(result.Stdout),
                string.Empty);
        }
        catch (JsonException ex)
        {
            return new TunnelStatusResult(
                false,
                null,
                $"Respuesta JSON inválida de tunnel-client: {ex.Message}");
        }
    }

    public Task<ProcessResult> ConnectAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        EnsureBinary(paths);
        return _runner.RunAsync(
            paths.TunnelClientPath,
            [
                "runtimes", "connect",
                "--alias", config.Alias,
                "--tunnel-id", config.TunnelId,
                "--runtime-api-key", $"file:{paths.RuntimeKeyPath}",
                "--profile", config.ProfileName,
                "--profile-dir", paths.ProfilesRoot,
                "--mcp-command", "LoomLCI.Host.exe",
                "--json"
            ],
            BuildEnvironment(paths, config),
            TimeSpan.FromSeconds(90),
            cancellationToken);
    }

    public Task<ProcessResult> StopAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        EnsureBinary(paths);
        return _runner.RunAsync(
            paths.TunnelClientPath,
            ["runtimes", "stop", config.Alias, "--json"],
            BuildEnvironment(paths, config),
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public Task<ProcessResult> InitProfileAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        EnsureBinary(paths);
        Directory.CreateDirectory(paths.ProfilesRoot);
        return _runner.RunAsync(
            paths.TunnelClientPath,
            [
                "init",
                "--profile", config.ProfileName,
                "--profile-dir", paths.ProfilesRoot,
                "--tunnel-id", config.TunnelId,
                "--control-plane-api-key-ref", $"file:{paths.RuntimeKeyPath}",
                "--mcp-command", "LoomLCI.Host.exe",
                "--health-listen-addr", "127.0.0.1:0",
                "--force"
            ],
            BuildEnvironment(paths, config),
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public Task<ProcessResult> DoctorAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        EnsureBinary(paths);
        return _runner.RunAsync(
            paths.TunnelClientPath,
            [
                "doctor",
                "--profile", config.ProfileName,
                "--profile-dir", paths.ProfilesRoot,
                "--explain",
                "--json"
            ],
            BuildEnvironment(paths, config),
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public IReadOnlyDictionary<string, string?> BuildEnvironment(
        AppPaths paths,
        MachineConfig config)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var hostDirectory = paths.VersionDirectory(config.ActiveVersion);

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TUNNEL_CLIENT_PROFILE_DIR"] = paths.ProfilesRoot,
            ["TUNNEL_CLIENT_STATE_DIR"] = paths.TunnelStateRoot,
            ["PATH"] = hostDirectory + Path.PathSeparator + currentPath
        };
    }

    public static void VerifyPinnedBinary(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Falta tunnel-client: {path}");
        }

        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();

        if (!string.Equals(
                hash,
                PinnedExecutableSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 inesperado para tunnel-client.exe: {hash}");
        }
    }

    private static void EnsureBinary(AppPaths paths)
    {
        if (!File.Exists(paths.TunnelClientPath))
        {
            throw new InvalidOperationException(
                $"Falta tunnel-client: {paths.TunnelClientPath}");
        }
    }

    private static string PreferError(ProcessResult result)
    {
        var stderr = result.Stderr.Trim();
        if (!string.IsNullOrEmpty(stderr))
        {
            return stderr;
        }

        var stdout = result.Stdout.Trim();
        return string.IsNullOrEmpty(stdout)
            ? $"exit code {result.ExitCode}"
            : stdout;
    }
}
