using System.ComponentModel;
using LoomLCI.Core;
using LoomLCI.Core.Processes;

namespace LoomLCI.Windows.Processes;

public sealed class WindowsProcessProvider : IProcessProvider
{
    private readonly bool _disableReleasePseudoConsole;

    public WindowsProcessProvider()
    {
    }

    internal WindowsProcessProvider(bool disableReleasePseudoConsole)
    {
        _disableReleasePseudoConsole = disableReleasePseudoConsole;
    }

    public Task<LoomResult<IProcessResource>> StartAsync(
        ProcessLaunchSpec spec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IProcessResource resource =
                WindowsNativeProcessLauncher.Launch(
                    spec,
                    _disableReleasePseudoConsole);

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
        catch (Exception ex) when (
            ex is NotSupportedException or
            PlatformNotSupportedException or
            EntryPointNotFoundException)
        {
            return Task.FromResult(
                LoomResult<IProcessResource>.Failure(
                    LoomErrors.Unsupported(ex.Message)));
        }
        catch (Exception ex) when (
            ex is Win32Exception or
            InvalidOperationException or
            IOException or
            System.Runtime.InteropServices.COMException)
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
