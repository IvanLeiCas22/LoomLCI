using System.Diagnostics;

namespace LoomLCI.Launcher;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"No se pudo iniciar {Path.GetFileName(executable)}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = timeout.HasValue
            ? new CancellationTokenSource(timeout.Value)
            : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (
            timeoutCts is not null &&
            timeoutCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException(
                $"{Path.GetFileName(executable)} excedió el tiempo límite.");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }
}
