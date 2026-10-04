using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Windows.Processes;

namespace LoomLCI.Windows.Tests;

public sealed class TerminalProcessCapabilityTests
{
    [Fact]
    public async Task TerminalProcessGetsRealConsoleAndConfiguredSize()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::WriteLine('IN_REDIRECTED=' + [Console]::IsInputRedirected); " +
                    "[Console]::WriteLine('OUT_REDIRECTED=' + [Console]::IsOutputRedirected); " +
                    "[Console]::WriteLine('SIZE=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight)"
                ],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(ProcessIoMode.Terminal, started.Value!.IoMode);

        var status = await WaitForExitAsync(
            fixture.Processes,
            started.Value.Handle);
        Assert.Equal(ManagedProcessState.Exited, status.State);
        Assert.Equal(0, status.ExitCode);
        Assert.Equal(ProcessIoMode.Terminal, status.IoMode);

        var read = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value.Handle,
            "SIZE=80x24");

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(ProcessIoMode.Terminal, read.Value!.IoMode);
        Assert.Null(read.Value.Stdout);
        Assert.Null(read.Value.Stderr);
        Assert.NotNull(read.Value.Terminal);

        var text = TerminalText(read.Value);
        Assert.Contains("IN_REDIRECTED=False", text, StringComparison.Ordinal);
        Assert.Contains("OUT_REDIRECTED=False", text, StringComparison.Ordinal);
        Assert.Contains("SIZE=80x24", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalInputRoundTripsThroughProcessWrite()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-Command",
                    "$line=[Console]::ReadLine(); [Console]::WriteLine('INPUT=' + $line)"
                ],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var written = await fixture.Processes.WriteAsync(
            started.Value!.Handle,
            "hello-conpty\r");
        Assert.True(written.IsSuccess, written.Error?.Message);

        var read = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value.Handle,
            "INPUT=hello-conpty");

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Contains(
            "INPUT=hello-conpty",
            TerminalText(read.Value!),
            StringComparison.Ordinal);

        var status = await WaitForExitAsync(
            fixture.Processes,
            started.Value.Handle);
        Assert.Equal(0, status.ExitCode);
    }

    [Fact]
    public async Task TerminalOutputPreservesRawVtAndUnicode()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        const string payload = "árbol-áéíóú";
        const string red = "\u001b[31m";

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::Write([char]27 + '[31márbol-áéíóú' + [char]27 + '[0m')"
                ],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var read = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value!.Handle,
            payload);

        Assert.True(read.IsSuccess, read.Error?.Message);
        var text = TerminalText(read.Value!);
        Assert.Contains(red, text, StringComparison.Ordinal);
        Assert.Contains(payload, text, StringComparison.Ordinal);
        Assert.Contains('\u001b', text);

        var status = await WaitForExitAsync(
            fixture.Processes,
            started.Value.Handle);
        Assert.Equal(0, status.ExitCode);
    }

    [Fact]
    public async Task TerminalResizeChangesConsoleDimensions()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::WriteLine('SIZE1=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight); " +
                    "$null=[Console]::ReadLine(); " +
                    "[Console]::WriteLine('SIZE2=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight)"
                ],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var initial = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value!.Handle,
            "SIZE1=80x24");
        Assert.True(initial.IsSuccess, initial.Error?.Message);

        var resized = await fixture.Processes.ResizeAsync(
            started.Value.Handle,
            100,
            30);
        Assert.True(resized.IsSuccess, resized.Error?.Message);

        var written = await fixture.Processes.WriteAsync(
            started.Value.Handle,
            "continue\r");
        Assert.True(written.IsSuccess, written.Error?.Message);

        var after = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value.Handle,
            "SIZE2=100x30");
        Assert.True(after.IsSuccess, after.Error?.Message);

        var status = await WaitForExitAsync(
            fixture.Processes,
            started.Value.Handle);
        Assert.Equal(0, status.ExitCode);
    }


    [Fact]
    public async Task NaturalTerminalRootExitKillsDescendantAndPreservesExitCode()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory =
            Directory.CreateTempSubdirectory("loom-terminal-root-exit-");
        var childPidPath =
            Path.Combine(directory.FullName, "child.pid");

        try
        {
            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-Command",
                        "$child = Start-Process powershell.exe " +
                        "-ArgumentList @('-NoProfile','-Command'," +
                        "'Start-Sleep -Seconds 60') -NoNewWindow -PassThru; " +
                        "[IO.File]::WriteAllText($env:LOOM_CHILD_PID, " +
                        "[string]$child.Id); " +
                        "Write-Output 'ROOT-EXIT'; exit 7"
                    ],
                    Environment: new Dictionary<string, string?>
                    {
                        ["LOOM_CHILD_PID"] = childPidPath
                    },
                    WorkId: work.Value!.Id,
                    IoMode: ProcessIoMode.Terminal));

            Assert.True(started.IsSuccess, started.Error?.Message);
            var childPid = await WaitForPidFileAsync(childPidPath);

            var status = await WaitForExitAsync(
                fixture.Processes,
                started.Value!.Handle);
            Assert.Equal(ManagedProcessState.Exited, status.State);
            Assert.Equal(7, status.ExitCode);

            await WaitForProcessGoneAsync(childPid);

            var read = await WaitForTerminalOutputAsync(
                fixture.Processes,
                started.Value.Handle,
                "ROOT-EXIT");
            Assert.True(read.IsSuccess, read.Error?.Message);
            Assert.Contains(
                "ROOT-EXIT",
                TerminalText(read.Value!),
                StringComparison.Ordinal);
            Assert.Equal(7, read.Value!.Process.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TerminateTerminalKillsTreeAndRetainsOutput()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory =
            Directory.CreateTempSubdirectory("loom-terminal-terminate-");
        var childPidPath =
            Path.Combine(directory.FullName, "child.pid");

        try
        {
            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-Command",
                        "$child = Start-Process powershell.exe " +
                        "-ArgumentList @('-NoProfile','-Command'," +
                        "'Start-Sleep -Seconds 60') -NoNewWindow -PassThru; " +
                        "[IO.File]::WriteAllText($env:LOOM_CHILD_PID, " +
                        "[string]$child.Id); " +
                        "Write-Output 'RETAINED-BEFORE-TERMINATE'; " +
                        "Start-Sleep -Seconds 60"
                    ],
                    Environment: new Dictionary<string, string?>
                    {
                        ["LOOM_CHILD_PID"] = childPidPath
                    },
                    WorkId: work.Value!.Id,
                    IoMode: ProcessIoMode.Terminal));

            Assert.True(started.IsSuccess, started.Error?.Message);
            var childPid = await WaitForPidFileAsync(childPidPath);

            var before = await WaitForTerminalOutputAsync(
                fixture.Processes,
                started.Value!.Handle,
                "RETAINED-BEFORE-TERMINATE");
            Assert.True(before.IsSuccess, before.Error?.Message);

            var terminated = await fixture.Processes.TerminateAsync(
                started.Value.Handle);
            Assert.True(terminated.IsSuccess, terminated.Error?.Message);

            await WaitForProcessGoneAsync(started.Value.ProcessId);
            await WaitForProcessGoneAsync(childPid);

            var after = await fixture.Processes.ReadAsync(
                started.Value.Handle,
                terminalCursor: 0);
            Assert.True(after.IsSuccess, after.Error?.Message);
            Assert.Contains(
                "RETAINED-BEFORE-TERMINATE",
                TerminalText(after.Value!),
                StringComparison.Ordinal);
            Assert.Equal(
                ManagedProcessState.Terminated,
                after.Value!.Process.State);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkCloseKillsSessionOwnedTerminalTree()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory =
            Directory.CreateTempSubdirectory("loom-terminal-work-close-");
        var childPidPath =
            Path.Combine(directory.FullName, "child.pid");

        try
        {
            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-Command",
                        "$child = Start-Process powershell.exe " +
                        "-ArgumentList @('-NoProfile','-Command'," +
                        "'Start-Sleep -Seconds 60') -NoNewWindow -PassThru; " +
                        "[IO.File]::WriteAllText($env:LOOM_CHILD_PID, " +
                        "[string]$child.Id); " +
                        "Start-Sleep -Seconds 60"
                    ],
                    Environment: new Dictionary<string, string?>
                    {
                        ["LOOM_CHILD_PID"] = childPidPath
                    },
                    WorkId: work.Value!.Id,
                    IoMode: ProcessIoMode.Terminal));

            Assert.True(started.IsSuccess, started.Error?.Message);
            var childPid = await WaitForPidFileAsync(childPidPath);

            var closed = await fixture.Sessions.CloseAsync(work.Value.Id);
            Assert.True(closed.IsSuccess, closed.Error?.Message);

            await WaitForProcessGoneAsync(started.Value!.ProcessId);
            await WaitForProcessGoneAsync(childPid);

            var status = await fixture.Processes.StatusAsync(
                started.Value.Handle);
            Assert.False(status.IsSuccess);
            Assert.Equal("resource_closed", status.Error?.Code);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task IndependentTerminalSurvivesWorkCloseAndRemainsInteractive()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        ProcessHandle? handle = null;

        try
        {
            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-Command",
                        "Write-Output 'READY'; " +
                        "$line=[Console]::ReadLine(); " +
                        "Write-Output ('LINE=' + $line); " +
                        "Write-Output ('SIZE=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight); " +
                        "$null=[Console]::ReadLine()"
                    ],
                    WorkId: work.Value!.Id,
                    Ownership: ResourceOwnership.Independent,
                    IoMode: ProcessIoMode.Terminal));

            Assert.True(started.IsSuccess, started.Error?.Message);
            handle = started.Value!.Handle;

            var ready = await WaitForTerminalOutputAsync(
                fixture.Processes,
                handle.Value,
                "READY");
            Assert.True(ready.IsSuccess, ready.Error?.Message);

            var closed = await fixture.Sessions.CloseAsync(work.Value.Id);
            Assert.True(closed.IsSuccess, closed.Error?.Message);

            var status = await fixture.Processes.StatusAsync(handle.Value);
            Assert.True(status.IsSuccess, status.Error?.Message);
            Assert.Equal(ManagedProcessState.Running, status.Value!.State);

            var resized = await fixture.Processes.ResizeAsync(
                handle.Value,
                90,
                25);
            Assert.True(resized.IsSuccess, resized.Error?.Message);

            var written = await fixture.Processes.WriteAsync(
                handle.Value,
                "after-close\r");
            Assert.True(written.IsSuccess, written.Error?.Message);

            var output = await WaitForTerminalOutputAsync(
                fixture.Processes,
                handle.Value,
                "SIZE=90x25");
            Assert.True(output.IsSuccess, output.Error?.Message);
            Assert.Contains(
                "LINE=after-close",
                TerminalText(output.Value!),
                StringComparison.Ordinal);

            var finish = await fixture.Processes.WriteAsync(
                handle.Value,
                "done\r");
            Assert.True(finish.IsSuccess, finish.Error?.Message);

            var exited = await WaitForExitAsync(
                fixture.Processes,
                handle.Value);
            Assert.Equal(0, exited.ExitCode);
        }
        finally
        {
            if (handle is { } processHandle)
            {
                await fixture.Processes.TerminateAsync(processHandle);
                await fixture.Resources.CloseAsync(
                    processHandle.AsResourceHandle());
            }
        }
    }

    [Fact]
    public async Task CtrlCInterruptsCommandAndKeepsTerminalReusable()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile"],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var begin = await fixture.Processes.WriteAsync(
            started.Value!.Handle,
            "Write-Output READY; while ($true) { Start-Sleep -Seconds 1 }\r");
        Assert.True(begin.IsSuccess, begin.Error?.Message);

        var ready = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value.Handle,
            "READY");
        Assert.True(ready.IsSuccess, ready.Error?.Message);

        var interrupt = await fixture.Processes.WriteAsync(
            started.Value.Handle,
            "\u0003");
        Assert.True(interrupt.IsSuccess, interrupt.Error?.Message);

        await Task.Delay(100);

        var after = await fixture.Processes.WriteAsync(
            started.Value.Handle,
            "Write-Output AFTER\r");
        Assert.True(after.IsSuccess, after.Error?.Message);

        var reusable = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value.Handle,
            "AFTER");
        Assert.True(reusable.IsSuccess, reusable.Error?.Message);

        var exit = await fixture.Processes.WriteAsync(
            started.Value.Handle,
            "exit\r");
        Assert.True(exit.IsSuccess, exit.Error?.Message);

        var status = await WaitForExitAsync(
            fixture.Processes,
            started.Value.Handle);
        Assert.Equal(0, status.ExitCode);
    }

    [Fact]
    public async Task LegacyPseudoConsoleCloseFallbackCompletes()
    {
        await using var fixture =
            new ProcessFixture(disableReleasePseudoConsole: true);
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Write-Output LEGACY-FALLBACK"],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var output = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value!.Handle,
            "LEGACY-FALLBACK");
        Assert.True(output.IsSuccess, output.Error?.Message);

        var status = await WaitForExitAsync(
            fixture.Processes,
            started.Value.Handle);
        Assert.Equal(0, status.ExitCode);

        var closed = await fixture.Resources.CloseAsync(
                started.Value.Handle.AsResourceHandle())
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(closed.IsSuccess, closed.Error?.Message);
    }

    [Fact]
    public async Task ConcurrentWriteResizeAndTerminateDoNotRaceWithTeardown()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-Command",
                    "Write-Output READY; Start-Sleep -Seconds 60"
                ],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var ready = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value!.Handle,
            "READY");
        Assert.True(ready.IsSuccess, ready.Error?.Message);

        var operations = Enumerable.Range(0, 20)
            .Select(i => i % 2 == 0
                ? fixture.Processes.ResizeAsync(
                    started.Value.Handle,
                    80 + i,
                    24 + (i % 5))
                : fixture.Processes.WriteAsync(
                    started.Value.Handle,
                    $"input-{i}\r"))
            .ToArray();

        var terminateTask = fixture.Processes.TerminateAsync(
            started.Value.Handle);

        var results = await Task.WhenAll(operations);
        var terminated = await terminateTask;

        Assert.True(terminated.IsSuccess, terminated.Error?.Message);
        Assert.All(
            results,
            result => Assert.True(
                result.IsSuccess ||
                result.Error?.Code is "conflict" or "execution_failed",
                result.Error?.Message));

        var status = await fixture.Processes.StatusAsync(
            started.Value.Handle);
        Assert.True(status.IsSuccess, status.Error?.Message);
        Assert.Equal(
            ManagedProcessState.Terminated,
            status.Value!.State);
    }


    [Fact]
    public async Task RepeatedShortTerminalLifecycleCleansRetainedSpoolsOnClose()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        for (var i = 0; i < 6; i++)
        {
            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-Command",
                        $"Write-Output LOOP-{i}"
                    ],
                    WorkId: work.Value!.Id,
                    IoMode: ProcessIoMode.Terminal));

            Assert.True(started.IsSuccess, started.Error?.Message);

            var output = await WaitForTerminalOutputAsync(
                fixture.Processes,
                started.Value!.Handle,
                $"LOOP-{i}");
            Assert.True(output.IsSuccess, output.Error?.Message);

            var status = await WaitForExitAsync(
                fixture.Processes,
                started.Value.Handle);
            Assert.Equal(0, status.ExitCode);

            var resolved = fixture.Resources.Resolve<IProcessResource>(
                started.Value.Handle.AsResourceHandle(),
                ProcessCapability.ResourceKind);
            Assert.True(resolved.IsSuccess, resolved.Error?.Message);

            var resource = Assert.IsType<WindowsProcessResource>(
                resolved.Value!.Resource);
            var spoolPath = resource.TerminalSpoolPath;
            Assert.True(File.Exists(spoolPath));

            var closed = await fixture.Resources.CloseAsync(
                    started.Value.Handle.AsResourceHandle())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(closed.IsSuccess, closed.Error?.Message);
            Assert.False(File.Exists(spoolPath));
        }
    }


    [Fact]
    public async Task ConcurrentWriteResizeAndWorkCloseDoNotRaceWithTeardown()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-Command",
                    "Write-Output READY; Start-Sleep -Seconds 60"
                ],
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var ready = await WaitForTerminalOutputAsync(
            fixture.Processes,
            started.Value!.Handle,
            "READY");
        Assert.True(ready.IsSuccess, ready.Error?.Message);

        var operations = Enumerable.Range(0, 20)
            .Select(i => i % 2 == 0
                ? fixture.Processes.ResizeAsync(
                    started.Value.Handle,
                    80 + i,
                    24 + (i % 5))
                : fixture.Processes.WriteAsync(
                    started.Value.Handle,
                    $"work-close-{i}\r"))
            .ToArray();

        var closeTask = fixture.Sessions.CloseAsync(work.Value.Id).AsTask();

        var results = await Task.WhenAll(operations);
        var closed = await closeTask;

        Assert.True(closed.IsSuccess, closed.Error?.Message);
        Assert.All(
            results,
            result => Assert.True(
                result.IsSuccess ||
                result.Error?.Code is
                    "conflict" or
                    "execution_failed" or
                    "resource_closed" or
                    "cancelled",
                result.Error?.Message));

        var status = await fixture.Processes.StatusAsync(
            started.Value.Handle);
        Assert.False(status.IsSuccess);
        Assert.Equal("resource_closed", status.Error?.Code);
    }

    private static string TerminalText(ProcessOutputReadResult result)
        => string.Concat(
            result.Terminal!.Chunks.Select(chunk => chunk.Text));

    private static async Task<ProcessStatusResult> WaitForExitAsync(
        ProcessCapability processes,
        ProcessHandle handle)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await processes.StatusAsync(handle);
            Assert.True(status.IsSuccess, status.Error?.Message);

            if (status.Value!.State is
                ManagedProcessState.Exited or
                ManagedProcessState.Terminated)
            {
                return status.Value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Terminal process did not exit in time.");
    }

    private static async Task<LoomLCI.Core.LoomResult<ProcessOutputReadResult>>
        WaitForTerminalOutputAsync(
            ProcessCapability processes,
            ProcessHandle handle,
            string expected)
    {
        LoomLCI.Core.LoomResult<ProcessOutputReadResult>? latest = null;

        for (var i = 0; i < 200; i++)
        {
            latest = await processes.ReadAsync(
                handle,
                terminalCursor: 0);

            if (!latest.IsSuccess)
            {
                return latest;
            }

            var text = TerminalText(latest.Value!);
            if (text.Contains(expected, StringComparison.Ordinal))
            {
                return latest;
            }

            await Task.Delay(25);
        }

        return latest!;
    }


    private static async Task<int> WaitForPidFileAsync(string path)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path);
                if (int.TryParse(text, out var pid))
                {
                    return pid;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"PID file '{path}' was not written in time.");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process =
                global::System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitForProcessGoneAsync(int pid)
    {
        for (var i = 0; i < 200; i++)
        {
            if (!IsProcessAlive(pid))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Process {pid} remained alive.");
    }

    private sealed class ProcessFixture : IAsyncDisposable
    {
        public ProcessFixture(bool disableReleasePseudoConsole = false)
        {
            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(Resources, Events);
            Invocations = new InvocationRunner(Events, Sessions);
            Processes = new ProcessCapability(
                disableReleasePseudoConsole
                    ? new WindowsProcessProvider(
                        disableReleasePseudoConsole: true)
                    : new WindowsProcessProvider(),
                Resources,
                Invocations,
                Events);
        }

        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public ProcessCapability Processes { get; }

        public ValueTask DisposeAsync()
            => Sessions.DisposeAsync();
    }
}
