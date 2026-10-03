using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LoomLCI.Core;
using LoomLCI.Core.Processes;

namespace LoomLCI.Windows.Processes;

public sealed class WindowsProcessProvider : IProcessProvider
{
    public Task<LoomResult<IProcessResource>> StartAsync(
        ProcessLaunchSpec spec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (spec.IoMode != ProcessIoMode.Pipes)
        {
            return Task.FromResult(LoomResult<IProcessResource>.Failure(
                LoomErrors.Unsupported("Only pipe-based process I/O is implemented in milestone 1.")));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in spec.Environment)
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

        try
        {
            var process = new global::System.Diagnostics.Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                process.Dispose();
                return Task.FromResult(LoomResult<IProcessResource>.Failure(
                    LoomErrors.ExecutionFailed("Windows did not start the process.")));
            }

            IProcessResource resource;
            try
            {
                resource = new WindowsProcessResource(process);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                }
                catch (Exception cleanupEx) when (cleanupEx is InvalidOperationException or Win32Exception)
                {
                }

                process.Dispose();
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return CancelAfterStartAsync(resource, cancellationToken);
            }

            return Task.FromResult(LoomResult<IProcessResource>.Success(resource));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return Task.FromResult(LoomResult<IProcessResource>.Failure(LoomErrors.AccessDenied(ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(LoomResult<IProcessResource>.Failure(LoomErrors.AccessDenied(ex.Message)));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return Task.FromResult(LoomResult<IProcessResource>.Failure(
                LoomErrors.ExecutionFailed($"Could not start process: {ex.Message}")));
        }
    }

    private static async Task<LoomResult<IProcessResource>> CancelAfterStartAsync(
        IProcessResource resource,
        CancellationToken cancellationToken)
    {
        await resource.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return LoomResult<IProcessResource>.Failure(LoomErrors.Cancelled());
    }
}
