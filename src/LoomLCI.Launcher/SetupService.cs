using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LoomLCI.Launcher;

public sealed record SetupResult(
    AppPaths Paths,
    MachineConfig Config,
    string? StartShortcutPath,
    string? StopShortcutPath);

public sealed class SetupService
{
    private const long MaxTunnelClientArchiveBytes = 64L * 1024 * 1024;

    private readonly TunnelClient _tunnelClient;
    private readonly HttpClient _httpClient;

    public SetupService(
        TunnelClient tunnelClient,
        HttpClient? httpClient = null)
    {
        _tunnelClient = tunnelClient;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SetupResult> RunAsync(
        AppPaths defaultPaths,
        SetupOptions options,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ValidatePlatform();

        var packageRoot = Path.GetFullPath(
            options.PackageRoot ?? AppContext.BaseDirectory);
        var manifest = PortablePackageManifestStore.Load(packageRoot);

        var paths = new AppPaths(
            Path.GetFullPath(options.InstallRoot ?? defaultPaths.InstallRoot),
            Path.GetFullPath(options.DataRoot ?? defaultPaths.DataRoot));

        var tunnelId = ResolveTunnelId(options.TunnelId, input, output);
        var alias = string.IsNullOrWhiteSpace(options.Alias)
            ? "loomlci-installed"
            : options.Alias.Trim();
        ValidateAlias(alias);

        var runtimeKey = ResolveRuntimeKey(options.RuntimeKeyFile, input, output);
        using var deploymentLock = DeploymentLock.Acquire(paths);

        LocalDataOwnership.EnsureAvailable(paths);

        var currentConfig = File.Exists(paths.MachineConfigPath)
            ? MachineConfigStore.Load(paths.MachineConfigPath)
            : null;
        var sameVersion = string.Equals(
            currentConfig?.ActiveVersion,
            manifest.Version,
            StringComparison.Ordinal);

        if (sameVersion &&
            currentConfig!.ActiveSequence != manifest.Sequence)
        {
            throw new InvalidDataException(
                "El mismo nombre de versión no puede reutilizarse con otro sequence.");
        }

        var config = new MachineConfig
        {
            ActiveVersion = manifest.Version,
            ActiveSequence = manifest.Sequence,
            PreviousVersion = sameVersion
                ? currentConfig!.PreviousVersion
                : currentConfig?.ActiveVersion,
            PreviousSequence = sameVersion
                ? currentConfig!.PreviousSequence
                : currentConfig?.ActiveSequence ?? 0,
            HighestSequence = Math.Max(
                currentConfig?.HighestSequence ?? 0,
                manifest.Sequence),
            TunnelId = tunnelId,
            Alias = alias,
            ProfileName = alias,
            TunnelClientVersion = TunnelClient.PinnedVersion,
            TunnelClientArchiveSha256 = TunnelClient.PinnedArchiveSha256
        };

        output.WriteLine($"Preparando LoomLCI {manifest.Version}...");

        InstallHostAndLauncher(packageRoot, manifest, paths);
        await EnsureTunnelClientAsync(paths, output, cancellationToken);

        SecretFile.WriteUserOnly(paths.RuntimeKeyPath, runtimeKey);
        SecretFile.VerifyUserOnly(paths.RuntimeKeyPath);

        Directory.CreateDirectory(paths.TunnelStateRoot);
        Directory.CreateDirectory(paths.LogsRoot);

        var init = await _tunnelClient.InitProfileAsync(
            paths,
            config,
            cancellationToken);
        EnsureSuccess("tunnel-client init", init);

        EnsureProfileDoesNotContainSecret(
            Path.Combine(paths.ProfilesRoot, config.ProfileName + ".yaml"),
            runtimeKey);

        var doctor = await _tunnelClient.DoctorAsync(
            paths,
            config,
            cancellationToken);
        EnsureSuccess("tunnel-client doctor", doctor);
        EnsureDoctorOk(doctor.Stdout);

        MachineConfigStore.Save(paths.MachineConfigPath, config);
        LocalDataOwnership.Record(paths);

        string? startShortcutPath = null;
        string? stopShortcutPath = null;
        if (!options.NoShortcut)
        {
            var shortcuts = ShortcutCreator.CreateDesktopShortcuts(
                paths.LauncherPath);
            startShortcutPath = shortcuts.StartPath;
            stopShortcutPath = shortcuts.StopPath;
        }

        output.WriteLine("Setup completo. La instalación quedó preparada sin detener otro runtime.");
        return new SetupResult(
            paths,
            config,
            startShortcutPath,
            stopShortcutPath);
    }

    private static void ValidatePlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "LoomLCI portable sólo soporta Windows.");
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                $"Esta primera versión soporta Windows x64; arquitectura detectada: {RuntimeInformation.OSArchitecture}.");
        }
    }

    private static string ResolveTunnelId(
        string? requested,
        TextReader input,
        TextWriter output)
    {
        var value = requested;
        if (string.IsNullOrWhiteSpace(value))
        {
            output.Write("Tunnel ID: ");
            value = input.ReadLine();
        }

        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("tunnel_", StringComparison.Ordinal) ||
            value.Length <= "tunnel_".Length)
        {
            throw new ArgumentException("Tunnel ID inválido.");
        }

        return value;
    }

    private static string ResolveRuntimeKey(
        string? runtimeKeyFile,
        TextReader input,
        TextWriter output)
    {
        if (!string.IsNullOrWhiteSpace(runtimeKeyFile))
        {
            var path = Path.GetFullPath(runtimeKeyFile);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "No existe el archivo de runtime key.",
                    path);
            }

            var fromFile = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(fromFile))
            {
                throw new InvalidDataException(
                    "El archivo de runtime key está vacío.");
            }

            return fromFile;
        }

        if (!ReferenceEquals(input, Console.In))
        {
            output.Write("Runtime API key: ");
            var value = input.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("La runtime API key no puede estar vacía.");
            }

            return value;
        }

        output.Write("Runtime API key: ");
        var secret = ReadSecretFromConsole();
        output.WriteLine();

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("La runtime API key no puede estar vacía.");
        }

        return secret;
    }

    private static string ReadSecretFromConsole()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }

        return new string(chars.ToArray()).Trim();
    }

    private static void ValidateAlias(string alias)
    {
        if (alias.Length is < 1 or > 64 ||
            alias.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Alias inválido. Use sólo letras, números, '.', '_' o '-'.");
        }
    }

    private static void InstallHostAndLauncher(
        string packageRoot,
        PortablePackageManifest manifest,
        AppPaths paths)
    {
        HostPackageInstaller.InstallHost(
            packageRoot,
            manifest,
            paths);

        var sourceLauncher = Path.Combine(
            packageRoot,
            "LoomLCI.Launcher.exe");
        if (!File.Exists(sourceLauncher))
        {
            throw new InvalidOperationException(
                $"Paquete inválido: falta {sourceLauncher}.");
        }

        File.Copy(
            sourceLauncher,
            paths.LauncherPath,
            overwrite: true);
    }

    private async Task EnsureTunnelClientAsync(
        AppPaths paths,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.ToolsRoot);

        if (File.Exists(paths.TunnelClientPath))
        {
            TunnelClient.VerifyPinnedBinary(paths.TunnelClientPath);

            var version = await _tunnelClient.GetVersionAsync(
                paths,
                cancellationToken);

            if (!version.StartsWith(
                    TunnelClient.PinnedVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"tunnel-client instalado no coincide con {TunnelClient.PinnedVersion}: {version}");
            }

            return;
        }

        output.WriteLine($"Descargando tunnel-client {TunnelClient.PinnedVersion}...");

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.Setup",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var archivePath = Path.Combine(tempRoot, "tunnel-client.zip");
            await DownloadBoundedAsync(
                TunnelClient.PinnedArchiveUrl,
                archivePath,
                MaxTunnelClientArchiveBytes,
                cancellationToken);

            await using var archiveStream = File.OpenRead(archivePath);
            var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        archiveStream,
                        cancellationToken))
                .ToLowerInvariant();

            if (!string.Equals(
                    hash,
                    TunnelClient.PinnedArchiveSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-256 inesperado para tunnel-client: {hash}");
            }

            var extractRoot = Path.Combine(tempRoot, "extract");
            ZipFile.ExtractToDirectory(archivePath, extractRoot);

            var sourceExe = Directory
                .EnumerateFiles(
                    extractRoot,
                    "tunnel-client.exe",
                    SearchOption.AllDirectories)
                .SingleOrDefault()
                ?? throw new InvalidDataException(
                    "El ZIP oficial no contiene tunnel-client.exe.");

            File.Copy(sourceExe, paths.TunnelClientPath, overwrite: false);
            TunnelClient.VerifyPinnedBinary(paths.TunnelClientPath);

            var version = await _tunnelClient.GetVersionAsync(
                paths,
                cancellationToken);
            if (!version.StartsWith(
                    TunnelClient.PinnedVersion,
                    StringComparison.Ordinal))
            {
                File.Delete(paths.TunnelClientPath);
                throw new InvalidDataException(
                    $"El binario descargado reporta versión inesperada: {version}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private async Task DownloadBoundedAsync(
        string url,
        string destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long declared &&
            declared > maxBytes)
        {
            throw new InvalidDataException(
                $"El archive de tunnel-client excede {maxBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var target = File.Create(destination);

        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"El archive de tunnel-client excede {maxBytes} bytes.");
            }

            await target.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }

    private static void EnsureProfileDoesNotContainSecret(
        string profilePath,
        string secret)
    {
        if (!File.Exists(profilePath))
        {
            throw new InvalidOperationException(
                $"tunnel-client init no creó {profilePath}.");
        }

        var profile = File.ReadAllText(profilePath);
        if (profile.Contains(secret, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "El profile contiene la runtime key literal.");
        }
    }

    private static void EnsureDoctorOk(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !string.Equals(
                result.GetString(),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "tunnel-client doctor no informó result=ok.");
        }
    }

    private static void EnsureSuccess(string operation, ProcessResult result)
    {
        if (result.Success)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout.Trim()
            : result.Stderr.Trim();

        throw new InvalidOperationException(
            $"{operation} falló (exit {result.ExitCode}): {detail}");
    }

}
