#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.Runtime.InteropServices;
using Windows.Win32;
using Win32Coord = Windows.Win32.System.Console.COORD;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsPseudoConsole : IDisposable
{
    private readonly object _gate = new();
    private readonly ClosePseudoConsoleSafeHandle _handle;
    private bool _disposed;

    private WindowsPseudoConsole(ClosePseudoConsoleSafeHandle handle)
    {
        _handle = handle;
    }

    internal IntPtr DangerousGetHandle()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handle.DangerousGetHandle();
        }
    }

    public static unsafe WindowsPseudoConsole Create(
        SafeHandle input,
        SafeHandle output,
        int columns,
        int rows)
    {
        var size = CreateSize(columns, rows);
        var result = PInvoke.CreatePseudoConsole(
            size,
            input,
            output,
            0,
            out var handle);

        if (result.Failed)
        {
            handle.Dispose();
            result.ThrowOnFailure();
        }

        return new WindowsPseudoConsole(handle);
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var result = PInvoke.ResizePseudoConsole(
                _handle,
                CreateSize(columns, rows));
            result.ThrowOnFailure();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Dispose();
        }
    }

    private static Win32Coord CreateSize(int columns, int rows)
        => new()
        {
            X = checked((short)columns),
            Y = checked((short)rows)
        };
}
