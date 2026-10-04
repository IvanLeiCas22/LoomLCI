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

    private sealed class ProcessFixture : IAsyncDisposable
    {
        public ProcessFixture()
        {
            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(Resources, Events);
            Invocations = new InvocationRunner(Events, Sessions);
            Processes = new ProcessCapability(
                new WindowsProcessProvider(),
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
