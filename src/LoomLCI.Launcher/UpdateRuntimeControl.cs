namespace LoomLCI.Launcher;

public interface IUpdateRuntimeControl
{
    Task StopAndConfirmAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken);

    Task StartAndConfirmAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken);

    Task<bool> IsReadyAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken);
}

public sealed class TunnelUpdateRuntimeControl : IUpdateRuntimeControl
{
    private readonly TunnelClient _tunnelClient;

    public TunnelUpdateRuntimeControl(TunnelClient tunnelClient)
    {
        _tunnelClient = tunnelClient;
    }

    public async Task StopAndConfirmAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        var before = await _tunnelClient.StatusAsync(
            paths,
            config,
            cancellationToken);

        if (IsStopped(before))
        {
            return;
        }

        var stop = await _tunnelClient.StopAsync(
            paths,
            config,
            cancellationToken);

        if (!stop.Success)
        {
            throw new InvalidOperationException(
                "No se pudo detener el runtime antes del update: " +
                PreferError(stop));
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await _tunnelClient.StatusAsync(
                paths,
                config,
                cancellationToken);
            if (IsStopped(status))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            "El runtime no confirmó estado detenido.");
    }

    public async Task StartAndConfirmAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        var connect = await _tunnelClient.ConnectAsync(
            paths,
            config,
            cancellationToken);

        if (!connect.Success)
        {
            throw new InvalidOperationException(
                "No se pudo iniciar el runtime actualizado: " +
                PreferError(connect));
        }

        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsReadyAsync(
                    paths,
                    config,
                    cancellationToken))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            "El runtime actualizado no quedó healthy/ready.");
    }

    public async Task<bool> IsReadyAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        var status = await _tunnelClient.StatusAsync(
            paths,
            config,
            cancellationToken);

        return status.CommandSucceeded &&
               status.Status?.IsReady == true &&
               string.Equals(
                   status.Status.TunnelId,
                   config.TunnelId,
                   StringComparison.Ordinal);
    }

    private static bool IsStopped(TunnelStatusResult result) =>
        (result.CommandSucceeded &&
         result.Status is { ProcessRunning: false }) ||
        (!result.CommandSucceeded &&
         result.ErrorText.Contains(
             "is not known",
             StringComparison.OrdinalIgnoreCase));

    private static string PreferError(ProcessResult result)
    {
        var stderr = result.Stderr.Trim();
        return !string.IsNullOrEmpty(stderr)
            ? stderr
            : result.Stdout.Trim();
    }
}
