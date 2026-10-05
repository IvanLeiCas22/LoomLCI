namespace LoomLCI.Launcher;

public sealed class LauncherApplication
{
    private readonly AppPaths _paths;
    private readonly TunnelClient _tunnelClient;
    private readonly SetupService _setup;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public LauncherApplication(
        AppPaths paths,
        TunnelClient tunnelClient,
        SetupService setup,
        TextWriter output,
        TextWriter error)
    {
        _paths = paths;
        _tunnelClient = tunnelClient;
        _setup = setup;
        _output = output;
        _error = error;
    }

    public async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var command = args.Length == 0
            ? "start"
            : args[0].ToLowerInvariant();

        try
        {
            if (command == "start" &&
                !File.Exists(_paths.MachineConfigPath) &&
                File.Exists(Path.Combine(AppContext.BaseDirectory, "package.json")))
            {
                _output.WriteLine("Primera ejecución: iniciando setup de LoomLCI.");
                return await SetupAsync([], cancellationToken);
            }

            return command switch
            {
                "start" => await StartAsync(cancellationToken),
                "stop" => await StopAsync(cancellationToken),
                "status" => await StatusAsync(cancellationToken),
                "setup" => await SetupAsync(args[1..], cancellationToken),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            TimeoutException or
            HttpRequestException)
        {
            _error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        var config = ValidateInstalledState();

        var before = await _tunnelClient.StatusAsync(
            _paths,
            config,
            cancellationToken);

        if (before.Status?.IsReady == true)
        {
            _output.WriteLine(
                $"LoomLCI ya está listo ({config.ActiveVersion}, alias {config.Alias}).");
            return 0;
        }

        _output.WriteLine("Iniciando LoomLCI...");
        var connect = await _tunnelClient.ConnectAsync(
            _paths,
            config,
            cancellationToken);

        if (!connect.Success)
        {
            _error.WriteLine(
                "No se pudo iniciar LoomLCI: " +
                PreferError(connect));
            return 1;
        }

        var after = await _tunnelClient.StatusAsync(
            _paths,
            config,
            cancellationToken);

        if (after.Status?.IsReady != true)
        {
            _error.WriteLine(
                "tunnel-client arrancó, pero LoomLCI no quedó healthy/ready. " +
                (after.ErrorText.Length > 0
                    ? after.ErrorText
                    : FormatStatus(after.Status)));
            return 1;
        }

        if (!string.Equals(
                after.Status.TunnelId,
                config.TunnelId,
                StringComparison.Ordinal))
        {
            _error.WriteLine(
                $"El runtime listo reporta un tunnel inesperado: {after.Status.TunnelId}.");
            return 1;
        }

        _output.WriteLine(
            $"LoomLCI está listo ({config.ActiveVersion}, alias {config.Alias}).");
        return 0;
    }

    private async Task<int> StopAsync(CancellationToken cancellationToken)
    {
        var config = ValidateInstalledState();
        var status = await _tunnelClient.StatusAsync(
            _paths,
            config,
            cancellationToken);

        if ((status.CommandSucceeded &&
             status.Status is { ProcessRunning: false }) ||
            (!status.CommandSucceeded &&
             status.ErrorText.Contains("is not known", StringComparison.OrdinalIgnoreCase)))
        {
            _output.WriteLine("LoomLCI ya está detenido.");
            return 0;
        }

        var stop = await _tunnelClient.StopAsync(
            _paths,
            config,
            cancellationToken);

        if (!stop.Success)
        {
            _error.WriteLine(
                "No se pudo detener LoomLCI: " + PreferError(stop));
            return 1;
        }

        _output.WriteLine("LoomLCI detenido.");
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var config = ValidateInstalledState();
        var version = await _tunnelClient.GetVersionAsync(
            _paths,
            cancellationToken);

        var status = await _tunnelClient.StatusAsync(
            _paths,
            config,
            cancellationToken);

        _output.WriteLine($"LoomLCI version: {config.ActiveVersion}");
        _output.WriteLine($"tunnel-client: {version}");
        _output.WriteLine($"alias: {config.Alias}");

        if (!status.CommandSucceeded || status.Status is null)
        {
            _output.WriteLine("estado: detenido/no registrado");
            if (!string.IsNullOrWhiteSpace(status.ErrorText))
            {
                _output.WriteLine($"detalle: {status.ErrorText}");
            }

            return 0;
        }

        _output.WriteLine(
            status.Status.IsReady
                ? "estado: listo"
                : $"estado: {status.Status.RuntimeState}");
        _output.WriteLine($"process_running: {status.Status.ProcessRunning}");
        _output.WriteLine($"healthy: {status.Status.Healthy}");
        _output.WriteLine($"ready: {status.Status.Ready}");
        _output.WriteLine($"tunnel: {status.Status.TunnelId}");

        return status.Status.IsReady ? 0 : 1;
    }

    private async Task<int> SetupAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var options = SetupOptions.Parse(args);
        var result = await _setup.RunAsync(
            _paths,
            options,
            Console.In,
            _output,
            cancellationToken);

        _output.WriteLine($"Instalado en: {result.Paths.InstallRoot}");
        _output.WriteLine($"Datos locales: {result.Paths.DataRoot}");
        if (result.ShortcutPath is not null)
        {
            _output.WriteLine($"Acceso directo: {result.ShortcutPath}");
        }

        _output.WriteLine(
            "El próximo paso es un cutover controlado; setup no detuvo el runtime legacy.");
        return 0;
    }

    private MachineConfig ValidateInstalledState()
    {
        var config = MachineConfigStore.Load(_paths.MachineConfigPath);

        if (!File.Exists(_paths.LauncherPath))
        {
            throw new InvalidOperationException(
                $"Falta Launcher instalado: {_paths.LauncherPath}");
        }

        if (!File.Exists(_paths.HostPath(config.ActiveVersion)))
        {
            throw new InvalidOperationException(
                $"Falta Host activo: {_paths.HostPath(config.ActiveVersion)}");
        }

        TunnelClient.VerifyPinnedBinary(_paths.TunnelClientPath);

        if (!string.Equals(
                config.TunnelClientVersion,
                TunnelClient.PinnedVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                config.TunnelClientArchiveSha256,
                TunnelClient.PinnedArchiveSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "machine.json declara una versión de tunnel-client no soportada.");
        }

        if (!File.Exists(_paths.RuntimeKeyPath))
        {
            throw new InvalidOperationException(
                $"Falta runtime key: {_paths.RuntimeKeyPath}");
        }

        SecretFile.VerifyUserOnly(_paths.RuntimeKeyPath);
        return config;
    }

    private int PrintHelp()
    {
        _output.WriteLine("LoomLCI.Launcher");
        _output.WriteLine("  start   Inicia LoomLCI y verifica health/ready.");
        _output.WriteLine("  stop    Detiene sólo el runtime local.");
        _output.WriteLine("  status  Muestra el estado del runtime instalado.");
        _output.WriteLine("  setup   Instala/configura el paquete portable sin hacer cutover.");
        _output.WriteLine();
        _output.WriteLine("setup options:");
        _output.WriteLine("  --tunnel-id <id>");
        _output.WriteLine("  --runtime-key-file <path>  (evita pasar secretos por command line)");
        _output.WriteLine("  --alias <name>");
        _output.WriteLine("  --no-shortcut");
        return 0;
    }

    private int UnknownCommand(string command)
    {
        _error.WriteLine($"Comando desconocido: {command}");
        return 2;
    }

    private static string PreferError(ProcessResult result)
    {
        var stderr = result.Stderr.Trim();
        return !string.IsNullOrEmpty(stderr)
            ? stderr
            : result.Stdout.Trim();
    }

    private static string FormatStatus(TunnelRuntimeStatus? status) =>
        status is null
            ? "sin status"
            : $"state={status.RuntimeState}, process={status.ProcessRunning}, healthy={status.Healthy}, ready={status.Ready}";
}
