using System.ComponentModel;
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
            return Task.FromResult(
                LoomResult<IProcessResource>.Failure(
                    LoomErrors.Unsupported(
                        "Only pipe-based process I/O is implemented.")));
        }

        try
        {
            IProcessResource resource =
                WindowsNativeProcessLauncher.Launch(spec);

            if (cancellationToken.IsCancellationRequested)
            {
                return CancelAfterStartAsync(
                    resource,
                    cancellationToken);
            }

            return Task.FromResult(
                LoomResult<IProcessResource>.Success(resource));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return Task.FromResult(
                LoomResult<IProcessResource>.Failure(
                    LoomErrors.AccessDenied(ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(
                LoomResult<IProcessResource>.Failure(
                    LoomErrors.AccessDenied(ex.Message)));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(
                LoomResult<IProcessResource>.Failure(
                    LoomErrors.InvalidArgument(ex.Message)));
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(
                LoomResult<IProcessResource>.Failure(
                    LoomErrors.Unsupported(ex.Message)));
        }
        catch (Exception ex) when (
            ex is Win32Exception or
            InvalidOperationException or
            IOException)
        {
            return Task.FromResult(
                LoomResult<IProcessResource>.Failure(
                    LoomErrors.ExecutionFailed(
                        $"Could not start process: {ex.Message}")));
        }
    }

    private static async Task<LoomResult<IProcessResource>>
        CancelAfterStartAsync(
            IProcessResource resource,
            CancellationToken cancellationToken)
    {
        await resource.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return LoomResult<IProcessResource>.Failure(
            LoomErrors.Cancelled());
    }
}
