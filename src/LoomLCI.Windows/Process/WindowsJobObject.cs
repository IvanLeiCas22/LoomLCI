#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsJobObject : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private WindowsJobObject(SafeFileHandle handle)
    {
        _handle = handle;
    }

    internal SafeFileHandle Handle => _handle;

    public static WindowsJobObject Create()
    {
        var handle = PInvoke.CreateJobObject(lpJobAttributes: null, lpName: null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create Windows Job Object.");
        }

        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation =
                {
                    LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var bytes = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref limits, 1));
            if (!PInvoke.SetInformationJobObject(
                    handle,
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    bytes))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not configure Windows Job Object.");
            }

            return new WindowsJobObject(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Terminate(uint exitCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!PInvoke.TerminateJobObject(_handle, exitCode))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not terminate Windows Job Object.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
