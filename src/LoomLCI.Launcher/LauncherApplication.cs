namespace LoomLCI.Launcher;

public sealed class LauncherApplication
{
    private readonly AppPaths _paths;
    private readonly TunnelClient _tunnelClient;
    private readonly SetupService _setup;
    private readonly UpdateService _updates;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public LauncherApplication(
        AppPaths paths,
        TunnelClient tunnelClient,
        SetupService setup,
        UpdateService updates,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        _paths = paths;
        _tunnelClient = tunnelClient;
        _setup = setup;
        _updates = updates;
        _input = input;
        _output = output;
        _error = error;
    }

    public async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var pause = args.Any(arg =>
            string.Equals(arg, "--pause", StringComparison.OrdinalIgnoreCase));
        var effectiveArgs = args
            .Where(arg => !string.Equals(
                arg,
                "--pause",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var command = effectiveArgs.Length == 0
            ? "start"
            : effectiveArgs[0].ToLowerInvariant();

        int exitCode;
        try
        {
            if (command == "start" &&
                !File.Exists(_paths.MachineConfigPath) &&
                File.Exists(Path.Combine(AppContext.BaseDirectory, "package.json")))
            {
                _output.WriteLine("Primera ejecución: iniciando setup de LoomLCI.");
                exitCode = await SetupAsync([], cancellationToken);
            }
            else
            {
                exitCode = command switch
                {
                    "start" => await StartAsync(cancellationToken),
                    "stop" => await StopAsync(cancellationToken),
                    "status" => await StatusAsync(cancellationToken),
                    "setup" => await SetupAsync(effectiveArgs[1..], cancellationToken),
                    "update" => await UpdateAsync(effectiveArgs[1..], cancellationToken),
                    "rollback" => await RollbackAsync(effectiveArgs[1..], cancellationToken),
                    "help" or "--help" or "-h" => PrintHelp(),
                    _ => UnknownCommand(command)
                };
            }
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
            exitCode = 1;
        }

        if (pause)
        {
            await PauseBeforeExitAsync(cancellationToken);
        }

        return exitCode;
    }

    private async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        await _updates.RecoverIfNeededAsync(cancellationToken);
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
        _output.WriteLine($"sequence: {config.ActiveSequence}");
        if (!string.IsNullOrWhiteSpace(config.PreviousVersion))
        {
            _output.WriteLine(
                $"previous: {config.PreviousVersion} (sequence {config.PreviousSequence})");
        }
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


    private async Task<int> UpdateAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException(
                "Uso: LoomLCI.Launcher.exe update <check|apply>.");
        }

        switch (args[0].ToLowerInvariant())
        {
            case "check":
            {
                var check = await _updates.CheckAsync(
                    cancellationToken);

                _output.WriteLine(
                    $"Versión actual: {check.CurrentVersion} " +
                    $"(sequence {check.CurrentSequence})");
                _output.WriteLine(
                    $"Última release: {check.Release.Version} " +
                    $"(sequence {check.Release.Sequence})");

                if (check.RequiresNewInstaller)
                {
                    _output.WriteLine(
                        "La release requiere un Launcher/installer más nuevo.");
                    return 3;
                }

                _output.WriteLine(
                    check.UpdateAvailable
                        ? "Hay una actualización disponible."
                        : "LoomLCI ya está actualizado.");
                return 0;
            }

            case "apply":
            {
                var result = await _updates.ApplyAsync(
                    cancellationToken,
                    progress =>
                    {
                        var percent = 100.0 * progress.BytesReceived /
                            progress.TotalBytes;
                        var received = progress.BytesReceived / 1048576.0;
                        var total = progress.TotalBytes / 1048576.0;
                        var speed = progress.BytesPerSecond / 1024.0;
                        _output.WriteLine(
                            $"Descarga intento {progress.Attempt}: " +
                            $"{percent:F1}% ({received:F1}/{total:F1} MiB), " +
                            $"{speed:F0} KiB/s");
                        _output.Flush();
                    });

                if (!result.Changed)
                {
                    _output.WriteLine(
                        $"LoomLCI ya está actualizado ({result.ActiveVersion}).");
                    return 0;
                }

                _output.WriteLine(
                    $"Update aplicado: {result.ActiveVersion} " +
                    $"(sequence {result.ActiveSequence}).");
                _output.WriteLine(
                    $"Rollback disponible: {result.PreviousVersion} " +
                    $"(sequence {result.PreviousSequence}).");
                return 0;
            }

            default:
                throw new ArgumentException(
                    $"Subcomando de update desconocido: {args[0]}.");
        }
    }

    private async Task<int> RollbackAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 0)
        {
            throw new ArgumentException(
                "Uso: LoomLCI.Launcher.exe rollback.");
        }

        var result = await _updates.RollbackAsync(
            cancellationToken);

        _output.WriteLine(
            $"Rollback aplicado: {result.ActiveVersion} " +
            $"(sequence {result.ActiveSequence}).");
        _output.WriteLine(
            $"Versión de retorno: {result.PreviousVersion} " +
            $"(sequence {result.PreviousSequence}).");
        return 0;
    }

    private async Task<int> SetupAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var options = SetupOptions.Parse(args);
        var result = await _setup.RunAsync(
            _paths,
            options,
            _input,
            _output,
            cancellationToken);

        _output.WriteLine("Instalación completada.");
        _output.WriteLine($"Instalado en: {result.Paths.InstallRoot}");
        _output.WriteLine($"Datos locales: {result.Paths.DataRoot}");
        if (result.StartShortcutPath is not null)
        {
            _output.WriteLine($"Acceso directo para iniciar: {result.StartShortcutPath}");
        }
        if (result.StopShortcutPath is not null)
        {
            _output.WriteLine($"Acceso directo para detener: {result.StopShortcutPath}");
        }

        _output.WriteLine(
            "La instalación quedó preparada. Si había otro runtime de LoomLCI en uso, no fue detenido automáticamente.");
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

        VersionName.Validate(config.ActiveVersion);
        if (!string.IsNullOrWhiteSpace(config.PreviousVersion))
        {
            VersionName.Validate(config.PreviousVersion);
        }

        if (config.ActiveSequence < 0 ||
            config.PreviousSequence < 0 ||
            config.HighestSequence < config.ActiveSequence)
        {
            throw new InvalidDataException(
                "machine.json contiene secuencias de update inválidas.");
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
        _output.WriteLine("  setup   Instala/configura el paquete portable sin detener otro runtime.");
        _output.WriteLine("  update check   Busca una release firmada más nueva.");
        _output.WriteLine("  update apply   Aplica update con rollback automático si falla.");
        _output.WriteLine("  rollback       Vuelve transaccionalmente a previousVersion.");
        _output.WriteLine();
        _output.WriteLine("opciones generales:");
        _output.WriteLine("  --pause  Espera Enter antes de cerrar; pensado para accesos directos.");
        _output.WriteLine();
        _output.WriteLine("setup options:");
        _output.WriteLine("  --tunnel-id <id>");
        _output.WriteLine("  --runtime-key-file <path>  (evita pasar secretos por command line)");
        _output.WriteLine("  --alias <name>");
        _output.WriteLine("  --no-shortcut");
        return 0;
    }

    private async Task PauseBeforeExitAsync(CancellationToken cancellationToken)
    {
        _output.WriteLine();
        _output.Write("Presione Enter para cerrar...");
        await _output.FlushAsync(cancellationToken);
        await _input.ReadLineAsync(cancellationToken);
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
