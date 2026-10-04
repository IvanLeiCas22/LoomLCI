#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LoomLCI.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace LoomLCI.Windows.Processes;

internal static class WindowsNativeProcessLauncher
{
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private static readonly Lock LaunchGate = new();

    public static unsafe WindowsProcessResource Launch(ProcessLaunchSpec spec)
    {
        if (spec.IoMode != ProcessIoMode.Pipes)
        {
            throw new NotSupportedException("Only pipe-based process I/O is implemented.");
        }

        var commandLine = WindowsCommandLine.Build(spec.Executable, spec.Arguments);
        if (commandLine.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Executable and arguments cannot contain NUL.");
        }

        var commandLineBuffer = (commandLine + '\0').ToCharArray();
        var environment = WindowsEnvironmentBlock.Create(spec.Environment);

        WindowsJobObject? job = null;
        AnonymousPipeServerStream? stdin = null;
        AnonymousPipeServerStream? stdout = null;
        AnonymousPipeServerStream? stderr = null;
        SafeFileHandle? processHandle = null;

        try
        {
            job = WindowsJobObject.Create();

            stdin = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            stdout = new AnonymousPipeServerStream(
                PipeDirection.In,
                HandleInheritability.Inheritable);
            stderr = new AnonymousPipeServerStream(
                PipeDirection.In,
                HandleInheritability.Inheritable);

            PROCESS_INFORMATION processInformation;

            lock (LaunchGate)
            {
                processInformation = CreateProcess(
                    spec,
                    commandLineBuffer,
                    environment,
                    job,
                    stdin,
                    stdout,
                    stderr);
            }

            processHandle = new SafeFileHandle(
                (IntPtr)processInformation.hProcess,
                ownsHandle: true);
            using var threadHandle = new SafeFileHandle(
                (IntPtr)processInformation.hThread,
                ownsHandle: true);

            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();

            var resource = new WindowsProcessResource(
                checked((int)processInformation.dwProcessId),
                processHandle,
                job,
                stdin,
                stdout,
                stderr);

            processHandle = null;
            job = null;
            stdin = null;
            stdout = null;
            stderr = null;

            return resource;
        }
        catch
        {
            processHandle?.Dispose();
            stdin?.Dispose();
            stdout?.Dispose();
            stderr?.Dispose();
            job?.Dispose();
            throw;
        }
    }

    private static unsafe PROCESS_INFORMATION CreateProcess(
        ProcessLaunchSpec spec,
        char[] commandLineBuffer,
        WindowsEnvironmentBlock environment,
        WindowsJobObject job,
        AnonymousPipeServerStream stdin,
        AnonymousPipeServerStream stdout,
        AnonymousPipeServerStream stderr)
    {
        nuint attributeListSize = 0;
        PInvoke.InitializeProcThreadAttributeList(
            default,
            2,
            ref attributeListSize);

        if (attributeListSize == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows did not provide a process attribute-list size.");
        }

        var attributeListMemory =
            Marshal.AllocHGlobal(checked((nint)attributeListSize));
        var attributeList =
            (LPPROC_THREAD_ATTRIBUTE_LIST)attributeListMemory;
        var attributeListInitialized = false;

        try
        {
            if (!PInvoke.InitializeProcThreadAttributeList(
                    attributeList,
                    2,
                    ref attributeListSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not initialize process attribute list.");
            }

            attributeListInitialized = true;

            var inheritedHandles = new nint[]
            {
                stdin.ClientSafePipeHandle.DangerousGetHandle(),
                stdout.ClientSafePipeHandle.DangerousGetHandle(),
                stderr.ClientSafePipeHandle.DangerousGetHandle()
            };

            var jobHandle = job.Handle.DangerousGetHandle();

            fixed (nint* inheritedHandlesPointer = inheritedHandles)
            {
                if (!PInvoke.UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        ProcThreadAttributeHandleList,
                        inheritedHandlesPointer,
                        checked((nuint)(inheritedHandles.Length * IntPtr.Size)),
                        null,
                        null))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not configure inherited process handles.");
                }
            }

            if (!PInvoke.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeJobList,
                    &jobHandle,
                    (nuint)IntPtr.Size,
                    null,
                    null))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not associate process creation with its Job Object.");
            }

            var startup = new STARTUPINFOEXW
            {
                lpAttributeList = attributeList
            };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            startup.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = (HANDLE)stdin.ClientSafePipeHandle.DangerousGetHandle();
            startup.StartupInfo.hStdOutput = (HANDLE)stdout.ClientSafePipeHandle.DangerousGetHandle();
            startup.StartupInfo.hStdError = (HANDLE)stderr.ClientSafePipeHandle.DangerousGetHandle();

            var creationFlags =
                PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT |
                PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;

            if (!environment.IsInherited)
            {
                creationFlags |= PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;
            }

            PROCESS_INFORMATION processInformation = default;

            fixed (char* commandLinePointer = commandLineBuffer)
            fixed (char* environmentPointer = environment.Characters)
            fixed (char* currentDirectoryPointer = spec.WorkingDirectory)
            {
                if (!PInvoke.CreateProcess(
                        default(PCWSTR),
                        commandLinePointer,
                        null,
                        null,
                        true,
                        creationFlags,
                        environmentPointer,
                        currentDirectoryPointer,
                        (STARTUPINFOW*)&startup,
                        &processInformation))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not start process '{spec.Executable}'.");
                }
            }

            return processInformation;
        }
        finally
        {
            if (attributeListInitialized)
            {
                PInvoke.DeleteProcThreadAttributeList(attributeList);
            }

            Marshal.FreeHGlobal(attributeListMemory);
        }
    }
}
