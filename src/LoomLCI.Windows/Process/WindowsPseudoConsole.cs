#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.Runtime.InteropServices;
using Windows.Win32;
using Win32Coord = Windows.Win32.System.Console.COORD;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsPseudoConsole : IDisposable
{
    private static readonly Lazy<bool> ReleasePseudoConsoleAvailable =
        new(DetectReleasePseudoConsole);

    private readonly object _gate = new();
    private readonly ClosePseudoConsoleSafeHandle _handle;
    private readonly bool _supportsReleasePseudoConsole;
    private bool _released;
    private bool _disposed;

    private WindowsPseudoConsole(
        ClosePseudoConsoleSafeHandle handle,
        bool supportsReleasePseudoConsole)
    {
        _handle = handle;
        _supportsReleasePseudoConsole = supportsReleasePseudoConsole;
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
        int rows,
        bool disableReleasePseudoConsole = false)
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

        return new WindowsPseudoConsole(
            handle,
            !disableReleasePseudoConsole &&
            ReleasePseudoConsoleAvailable.Value);
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

    public bool TryRelease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_released)
            {
                return true;
            }

            if (!_supportsReleasePseudoConsole)
            {
                return false;
            }

            try
            {
                var result = PInvoke.ReleasePseudoConsole(_handle);
                if (result.Failed)
                {
                    return false;
                }

                _released = true;
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
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

    private static bool DetectReleasePseudoConsole()
    {
        if (!NativeLibrary.TryLoad("kernel32.dll", out var library))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(
                library,
                "ReleasePseudoConsole",
                out _);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static Win32Coord CreateSize(int columns, int rows)
        => new()
        {
            X = checked((short)columns),
            Y = checked((short)rows)
        };
}
