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
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ForcedTerminationExitCode = 1;
    private static readonly Lock LaunchGate = new();

    public static WindowsProcessResource Launch(
        ProcessLaunchSpec spec,
        bool disableReleasePseudoConsole = false,
        TimeProvider? timeProvider = null,
        long? processMemoryLimitBytes = null)
    {
        timeProvider ??= TimeProvider.System;
        var commandLine = WindowsCommandLine.Build(
            spec.Executable,
            spec.Arguments);
        if (commandLine.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "Executable and arguments cannot contain NUL.");
        }

        var commandLineBuffer = (commandLine + '\0').ToCharArray();
        var environment = WindowsEnvironmentBlock.Create(spec.Environment);

        return spec.IoMode switch
        {
            ProcessIoMode.Pipes => LaunchPipes(
                spec,
                commandLineBuffer,
                environment,
                timeProvider,
                processMemoryLimitBytes),
            ProcessIoMode.Terminal => LaunchTerminal(
                spec,
                commandLineBuffer,
                environment,
                disableReleasePseudoConsole,
                timeProvider,
                processMemoryLimitBytes),
            _ => throw new NotSupportedException(
                $"Unsupported process I/O mode '{spec.IoMode}'.")
        };
    }

    private static WindowsProcessResource LaunchPipes(
        ProcessLaunchSpec spec,
        char[] commandLineBuffer,
        WindowsEnvironmentBlock environment,
        TimeProvider timeProvider,
        long? processMemoryLimitBytes)
    {
        WindowsJobObject? job = null;
        AnonymousPipeServerStream? stdin = null;
        AnonymousPipeServerStream? stdout = null;
        AnonymousPipeServerStream? stderr = null;
        SafeFileHandle? processHandle = null;
        IWindowsProcessIo? io = null;

        try
        {
            job = WindowsJobObject.Create(processMemoryLimitBytes);

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
                processInformation = CreatePipeProcess(
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

            io = new WindowsPipeProcessIo(
                stdin,
                stdout,
                stderr);
            stdin = null;
            stdout = null;
            stderr = null;

            var resource = new WindowsProcessResource(
                checked((int)processInformation.dwProcessId),
                processHandle,
                job,
                io,
                timeProvider);

            processHandle = null;
            job = null;
            io = null;

            return resource;
        }
        catch
        {
            DisposeIoSynchronously(io);
            processHandle?.Dispose();
            stdin?.Dispose();
            stdout?.Dispose();
            stderr?.Dispose();
            job?.Dispose();
            throw;
        }
    }

    private static WindowsProcessResource LaunchTerminal(
        ProcessLaunchSpec spec,
        char[] commandLineBuffer,
        WindowsEnvironmentBlock environment,
        bool disableReleasePseudoConsole,
        TimeProvider timeProvider,
        long? processMemoryLimitBytes)
    {
        if (spec.TerminalColumns is not { } columns ||
            spec.TerminalRows is not { } rows)
        {
            throw new ArgumentException(
                "Terminal dimensions are required for terminal I/O.");
        }

        WindowsJobObject? job = null;
        AnonymousPipeServerStream? input = null;
        AnonymousPipeServerStream? output = null;
        WindowsPseudoConsole? pseudoConsole = null;
        SafeFileHandle? processHandle = null;
        IWindowsProcessIo? io = null;

        try
        {
            job = WindowsJobObject.Create(processMemoryLimitBytes);

            input = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.None);
            output = new AnonymousPipeServerStream(
                PipeDirection.In,
                HandleInheritability.None);

            pseudoConsole = WindowsPseudoConsole.Create(
                input.ClientSafePipeHandle,
                output.ClientSafePipeHandle,
                columns,
                rows,
                disableReleasePseudoConsole);

            // CreatePseudoConsole duplicates/owns the ConPTY-side handles.
            // The host keeps only its write/read ends.
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();

            PROCESS_INFORMATION processInformation;

            lock (LaunchGate)
            {
                processInformation = CreateTerminalProcess(
                    spec,
                    commandLineBuffer,
                    environment,
                    job,
                    pseudoConsole);
            }

            processHandle = new SafeFileHandle(
                (IntPtr)processInformation.hProcess,
                ownsHandle: true);
            using var threadHandle = new SafeFileHandle(
                (IntPtr)processInformation.hThread,
                ownsHandle: true);

            io = new WindowsTerminalProcessIo(
                pseudoConsole,
                input,
                output);
            pseudoConsole = null;
            input = null;
            output = null;

            var resource = new WindowsProcessResource(
                checked((int)processInformation.dwProcessId),
                processHandle,
                job,
                io,
                timeProvider);

            processHandle = null;
            job = null;
            io = null;

            return resource;
        }
        catch
        {
            TryTerminate(job);
            processHandle?.Dispose();
            job?.Dispose();

            DisposeIoSynchronously(io);
            pseudoConsole?.Dispose();
            input?.Dispose();
            output?.Dispose();

            throw;
        }
    }

    private static unsafe PROCESS_INFORMATION CreatePipeProcess(
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
                        checked((nuint)(
                            inheritedHandles.Length * IntPtr.Size)),
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
            startup.StartupInfo.dwFlags =
                STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput =
                (HANDLE)stdin.ClientSafePipeHandle.DangerousGetHandle();
            startup.StartupInfo.hStdOutput =
                (HANDLE)stdout.ClientSafePipeHandle.DangerousGetHandle();
            startup.StartupInfo.hStdError =
                (HANDLE)stderr.ClientSafePipeHandle.DangerousGetHandle();

            var creationFlags =
                PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT |
                PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;

            if (!environment.IsInherited)
            {
                creationFlags |=
                    PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;
            }

            return CreateProcess(
                spec,
                commandLineBuffer,
                environment,
                startup,
                creationFlags,
                inheritHandles: true);
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

    private static unsafe PROCESS_INFORMATION CreateTerminalProcess(
        ProcessLaunchSpec spec,
        char[] commandLineBuffer,
        WindowsEnvironmentBlock environment,
        WindowsJobObject job,
        WindowsPseudoConsole pseudoConsole)
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

            var jobHandle = job.Handle.DangerousGetHandle();
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

            var pseudoConsoleHandle =
                pseudoConsole.DangerousGetHandle();
            if (!PInvoke.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    (void*)pseudoConsoleHandle,
                    (nuint)IntPtr.Size,
                    null,
                    null))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not associate process creation with its pseudoconsole.");
            }

            var startup = new STARTUPINFOEXW
            {
                lpAttributeList = attributeList
            };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);

            // Required when Loom itself has redirected stdio: explicitly
            // null std handles prevent Windows from copying the parent's
            // redirected handles over ConPTY.
            startup.StartupInfo.dwFlags =
                STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = default;
            startup.StartupInfo.hStdOutput = default;
            startup.StartupInfo.hStdError = default;

            var creationFlags =
                PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT;

            if (!environment.IsInherited)
            {
                creationFlags |=
                    PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;
            }

            return CreateProcess(
                spec,
                commandLineBuffer,
                environment,
                startup,
                creationFlags,
                inheritHandles: false);
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

    private static unsafe PROCESS_INFORMATION CreateProcess(
        ProcessLaunchSpec spec,
        char[] commandLineBuffer,
        WindowsEnvironmentBlock environment,
        STARTUPINFOEXW startup,
        PROCESS_CREATION_FLAGS creationFlags,
        bool inheritHandles)
    {
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
                    inheritHandles,
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

    private static void DisposeIoSynchronously(
        IWindowsProcessIo? io)
    {
        if (io is null)
        {
            return;
        }

        io.DisposeAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    private static void TryTerminate(WindowsJobObject? job)
    {
        if (job is null)
        {
            return;
        }

        try
        {
            job.Terminate(ForcedTerminationExitCode);
        }
        catch (Win32Exception)
        {
        }
    }
}
