using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Windows.Processes;

namespace LoomLCI.Windows.Tests;

public sealed class ProcessCapabilityTests
{
    [Fact]
    public async Task ShortProcessReturnsExitCodeAndNonDestructiveOutput()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "cmd.exe",
            ["/d", "/s", "/c", "echo loom-test & exit /b 7"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var handle = started.Value!.Handle;

        var status = await WaitForExitAsync(fixture.Processes, handle);
        Assert.Equal(ManagedProcessState.Exited, status.State);
        Assert.Equal(7, status.ExitCode);

        var first = await WaitForOutputAsync(fixture.Processes, handle, "loom-test");
        Assert.True(first.IsSuccess, first.Error?.Message);

        var again = await fixture.Processes.ReadAsync(handle, 0, 0);
        Assert.True(again.IsSuccess);
        Assert.Equal(
            string.Concat(first.Value!.Stdout.Chunks.Select(c => c.Text)),
            string.Concat(again.Value!.Stdout.Chunks.Select(c => c.Text)));

        var afterCursor = await fixture.Processes.ReadAsync(
            handle,
            first.Value.Stdout.NextCursor,
            first.Value.Stderr.NextCursor);

        Assert.True(afterCursor.IsSuccess);
        Assert.Empty(afterCursor.Value!.Stdout.Chunks);
    }

    [Fact]
    public async Task SmallProcessReadsDoNotSkipWithinCapturedOutput()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "powershell.exe",
            ["-NoProfile", "-Command", "[Console]::Out.Write('abcdef')"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var status = await WaitForExitAsync(fixture.Processes, started.Value!.Handle);
        Assert.Equal(0, status.ExitCode);

        var cursor = 0L;
        var output = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var read = await fixture.Processes.ReadAsync(
                started.Value.Handle,
                stdoutCursor: cursor,
                stderrCursor: 0,
                maxChars: 1);

            Assert.True(read.IsSuccess, read.Error?.Message);
            var chunk = Assert.Single(read.Value!.Stdout.Chunks);
            output.Add(chunk.Text);
            cursor = read.Value.Stdout.NextCursor;
        }

        Assert.Equal("abcdef", string.Concat(output));
        Assert.Equal(6, cursor);
    }

    [Fact]
    public async Task StdoutCanReuseUnusedStderrBudget()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "powershell.exe",
            ["-NoProfile", "-Command", "[Console]::Out.Write('abcdefghij')"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        await WaitForExitAsync(fixture.Processes, started.Value!.Handle);

        var read = await fixture.Processes.ReadAsync(
            started.Value.Handle,
            stdoutCursor: 0,
            stderrCursor: 0,
            maxChars: 10);

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal("abcdefghij", string.Concat(read.Value!.Stdout.Chunks.Select(chunk => chunk.Text)));
        Assert.Empty(read.Value.Stderr.Chunks);
    }

    [Fact]
    public async Task OutputBeyondOldOneMiBLimitRemainsRecoverableByCursor()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        const int outputLength = 1_200_000;
        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "powershell.exe",
            ["-NoProfile", "-Command", $"[Console]::Out.Write('A' * {outputLength})"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        await WaitForExitAsync(fixture.Processes, started.Value!.Handle);

        var cursor = 0L;
        var recovered = 0;
        while (cursor < outputLength)
        {
            var read = await fixture.Processes.ReadAsync(
                started.Value.Handle,
                stdoutCursor: cursor,
                stderrCursor: 0,
                maxChars: 256 * 1024);

            Assert.True(read.IsSuccess, read.Error?.Message);
            var stream = read.Value!.Stdout;
            var text = string.Concat(stream.Chunks.Select(chunk => chunk.Text));
            Assert.NotEmpty(text);
            Assert.All(text, character => Assert.Equal('A', character));
            Assert.False(stream.Truncated);
            Assert.False(stream.RetentionLimitReached);
            Assert.Equal(outputLength, stream.RetainedUntilCursor);
            Assert.Equal(outputLength, stream.ObservedUntilCursor);

            recovered += text.Length;
            cursor = stream.NextCursor;
        }

        Assert.Equal(outputLength, recovered);
        Assert.Equal(outputLength, cursor);
    }

    [Fact]
    public async Task SessionCloseTerminatesAndClosesLongRunningProcess()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var handle = started.Value!.Handle;
        var resolved = fixture.Resources.Resolve<IProcessResource>(handle.AsResourceHandle(), ProcessCapability.ResourceKind);
        Assert.True(resolved.IsSuccess, resolved.Error?.Message);
        var resource = Assert.IsType<WindowsProcessResource>(resolved.Value!.Resource);
        var stdoutSpool = resource.StdoutSpoolPath;
        var stderrSpool = resource.StderrSpoolPath;
        Assert.True(File.Exists(stdoutSpool));
        Assert.True(File.Exists(stderrSpool));

        var closed = await fixture.Sessions.CloseAsync(work.Value.Id);
        Assert.True(closed.IsSuccess);
        Assert.False(File.Exists(stdoutSpool));
        Assert.False(File.Exists(stderrSpool));

        var status = await fixture.Processes.StatusAsync(handle);
        Assert.False(status.IsSuccess);
        Assert.Equal("resource_closed", status.Error?.Code);
    }

    [Fact]
    public async Task ExplicitTerminateStopsLongRunningProcess()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var terminated = await fixture.Processes.TerminateAsync(started.Value!.Handle);
        Assert.True(terminated.IsSuccess, terminated.Error?.Message);

        var repeated = await fixture.Processes.TerminateAsync(started.Value.Handle);
        Assert.True(repeated.IsSuccess, repeated.Error?.Message);

        var status = await fixture.Processes.StatusAsync(started.Value.Handle);
        Assert.True(status.IsSuccess);
        Assert.Equal(ManagedProcessState.Terminated, status.Value!.State);
    }

    [Fact]
    public async Task TerminateAfterNaturalExitSucceedsWithoutChangingFinalState()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "cmd.exe",
            ["/d", "/s", "/c", "exit /b 7"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var exited = await WaitForExitAsync(fixture.Processes, started.Value!.Handle);
        Assert.Equal(ManagedProcessState.Exited, exited.State);
        Assert.Equal(7, exited.ExitCode);

        var terminated = await fixture.Processes.TerminateAsync(started.Value.Handle);
        Assert.True(terminated.IsSuccess, terminated.Error?.Message);

        var status = await fixture.Processes.StatusAsync(started.Value.Handle);
        Assert.True(status.IsSuccess, status.Error?.Message);
        Assert.Equal(ManagedProcessState.Exited, status.Value!.State);
        Assert.Equal(7, status.Value.ExitCode);
    }

    [Fact]
    public async Task CancelledStartDoesNotRegisterPartialResource()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
                WorkId: work.Value!.Id),
            cancellation.Token);

        Assert.False(started.IsSuccess);
        Assert.Equal("cancelled", started.Error?.Code);
        Assert.Equal(0, fixture.Resources.ActiveCount);
    }

    [Fact]
    public async Task DistinctProcessesCanRunConcurrently()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var starts = await Task.WhenAll(
            fixture.Processes.StartAsync(new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Milliseconds 150; Write-Output one"],
                WorkId: work.Value!.Id)),
            fixture.Processes.StartAsync(new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Milliseconds 150; Write-Output two"],
                WorkId: work.Value.Id)));

        Assert.All(starts, result => Assert.True(result.IsSuccess, result.Error?.Message));
        var first = Assert.IsType<ProcessStartResult>(starts[0].Value);
        var second = Assert.IsType<ProcessStartResult>(starts[1].Value);
        Assert.NotEqual(first.Handle, second.Handle);

        var exits = await Task.WhenAll(
            WaitForExitAsync(fixture.Processes, first.Handle),
            WaitForExitAsync(fixture.Processes, second.Handle));

        Assert.All(exits, status => Assert.Equal(0, status.ExitCode));
    }

    [Fact]
    public async Task ResourceCreationIsCorrelatedWithInvocationEvents()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "cmd.exe",
            ["/d", "/s", "/c", "exit /b 0"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var events = new List<LoomEvent>();
        while (fixture.Events.TryRead(out var loomEvent))
        {
            if (loomEvent is not null)
            {
                events.Add(loomEvent);
            }
        }

        var created = Assert.Single(
            events,
            e => e.Kind == "ResourceCreated" &&
                 e.ResourceHandle?.Value == started.Value!.Handle.Value);

        Assert.NotNull(created.InvocationId);
        Assert.Contains(events, e => e.Kind == "InvocationStarted" && e.InvocationId == created.InvocationId);
        Assert.Contains(events, e => e.Kind == "InvocationCompleted" && e.InvocationId == created.InvocationId);
    }

    [Fact]
    public async Task StdinCanDriveInteractivePipeProcess()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "cmd.exe",
            ["/d", "/q"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var handle = started.Value!.Handle;

        var write = await fixture.Processes.WriteAsync(handle, "echo stdin-ok\r\nexit /b 0\r\n");
        Assert.True(write.IsSuccess, write.Error?.Message);

        var status = await WaitForExitAsync(fixture.Processes, handle);
        Assert.Equal(0, status.ExitCode);

        var output = await WaitForOutputAsync(fixture.Processes, handle, "stdin-ok");
        Assert.True(output.IsSuccess);
    }


    [Fact]
    public async Task EnvironmentOverridesAndWorkingDirectoryArePreserved()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory = Directory.CreateTempSubdirectory("loom-native-process-");
        try
        {
            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-Command",
                        "[Console]::Out.Write($PWD.Path + '|' + $env:LOOMLCI_NATIVE_TEST + '|' + [string]::IsNullOrEmpty($env:PATH))"
                    ],
                    WorkingDirectory: directory.FullName,
                    Environment: new Dictionary<string, string?>
                    {
                        ["LOOMLCI_NATIVE_TEST"] = "value",
                        ["PATH"] = null
                    },
                    WorkId: work.Value!.Id));

            Assert.True(started.IsSuccess, started.Error?.Message);
            var status = await WaitForExitAsync(
                fixture.Processes,
                started.Value!.Handle);
            Assert.Equal(0, status.ExitCode);

            var read = await fixture.Processes.ReadAsync(
                started.Value.Handle,
                0,
                0);
            Assert.True(read.IsSuccess, read.Error?.Message);

            var stdout = string.Concat(
                read.Value!.Stdout.Chunks.Select(chunk => chunk.Text));
            Assert.Equal(
                $"{directory.FullName}|value|True",
                stdout);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExecutableLookupWithoutExeExtensionStillWorks()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "cmd",
                ["/d", "/s", "/c", "echo implicit-extension-ok"],
                WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var status = await WaitForExitAsync(
            fixture.Processes,
            started.Value!.Handle);
        Assert.Equal(0, status.ExitCode);

        var read = await WaitForOutputAsync(
            fixture.Processes,
            started.Value.Handle,
            "implicit-extension-ok");
        Assert.True(read.IsSuccess, read.Error?.Message);
    }

    [Fact]
    public async Task TerminateKillsRootChildAndGrandchildJobTree()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory = Directory.CreateTempSubdirectory("loom-job-tree-");
        var childPidPath = Path.Combine(directory.FullName, "child.pid");
        var grandchildPidPath =
            Path.Combine(directory.FullName, "grandchild.pid");
        var childScriptPath =
            Path.Combine(directory.FullName, "child.ps1");

        await File.WriteAllTextAsync(
            childScriptPath,
            "$psi = [Diagnostics.ProcessStartInfo]::new(); " +
            "$psi.FileName = 'powershell.exe'; " +
            "$psi.UseShellExecute = $false; " +
            "[void]$psi.ArgumentList.Add('-NoProfile'); " +
            "[void]$psi.ArgumentList.Add('-Command'); " +
            "[void]$psi.ArgumentList.Add('Start-Sleep -Seconds 60'); " +
            "$grandchild = [Diagnostics.Process]::Start($psi); " +
            "[IO.File]::WriteAllText($env:LOOM_GRANDCHILD_PID, " +
            "[string]$grandchild.Id); " +
            "Start-Sleep -Seconds 60");

        try
        {
            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-Command",
                        "$child = Start-Process powershell.exe " +
                        "-ArgumentList @('-NoProfile','-ExecutionPolicy'," +
                        "'Bypass','-File',$env:LOOM_CHILD_SCRIPT) " +
                        "-PassThru; " +
                        "[IO.File]::WriteAllText($env:LOOM_CHILD_PID, " +
                        "[string]$child.Id); " +
                        "Start-Sleep -Seconds 60"
                    ],
                    Environment: new Dictionary<string, string?>
                    {
                        ["LOOM_CHILD_SCRIPT"] = childScriptPath,
                        ["LOOM_CHILD_PID"] = childPidPath,
                        ["LOOM_GRANDCHILD_PID"] = grandchildPidPath
                    },
                    WorkId: work.Value!.Id));

            Assert.True(started.IsSuccess, started.Error?.Message);

            var childPid = await WaitForPidFileAsync(childPidPath);
            var grandchildPid =
                await WaitForPidFileAsync(grandchildPidPath);

            Assert.True(IsProcessAlive(started.Value!.ProcessId));
            Assert.True(IsProcessAlive(childPid));
            Assert.True(IsProcessAlive(grandchildPid));

            var terminated = await fixture.Processes.TerminateAsync(
                started.Value.Handle);
            Assert.True(terminated.IsSuccess, terminated.Error?.Message);

            var status = await fixture.Processes.StatusAsync(
                started.Value.Handle);
            Assert.True(status.IsSuccess, status.Error?.Message);
            Assert.Equal(
                ManagedProcessState.Terminated,
                status.Value!.State);

            await WaitForProcessGoneAsync(started.Value.ProcessId);
            await WaitForProcessGoneAsync(childPid);
            await WaitForProcessGoneAsync(grandchildPid);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TerminateAfterRootExitStillKillsRemainingChild()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory =
            Directory.CreateTempSubdirectory("loom-job-root-exit-");
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
                        "'Start-Sleep -Seconds 60') -PassThru; " +
                        "[IO.File]::WriteAllText($env:LOOM_CHILD_PID, " +
                        "[string]$child.Id)"
                    ],
                    Environment: new Dictionary<string, string?>
                    {
                        ["LOOM_CHILD_PID"] = childPidPath
                    },
                    WorkId: work.Value!.Id));

            Assert.True(started.IsSuccess, started.Error?.Message);
            var childPid = await WaitForPidFileAsync(childPidPath);

            var exited = await WaitForExitAsync(
                fixture.Processes,
                started.Value!.Handle);
            Assert.Equal(ManagedProcessState.Exited, exited.State);
            Assert.Equal(0, exited.ExitCode);
            Assert.True(IsProcessAlive(childPid));

            var terminated = await fixture.Processes.TerminateAsync(
                started.Value.Handle);
            Assert.True(terminated.IsSuccess, terminated.Error?.Message);

            var status = await fixture.Processes.StatusAsync(
                started.Value.Handle);
            Assert.True(status.IsSuccess, status.Error?.Message);
            Assert.Equal(
                ManagedProcessState.Exited,
                status.Value!.State);
            Assert.Equal(0, status.Value.ExitCode);

            await WaitForProcessGoneAsync(childPid);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkCloseKillsDescendantJobTree()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory =
            Directory.CreateTempSubdirectory("loom-job-work-close-");
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
                        "'Start-Sleep -Seconds 60') -PassThru; " +
                        "[IO.File]::WriteAllText($env:LOOM_CHILD_PID, " +
                        "[string]$child.Id); " +
                        "Start-Sleep -Seconds 60"
                    ],
                    Environment: new Dictionary<string, string?>
                    {
                        ["LOOM_CHILD_PID"] = childPidPath
                    },
                    WorkId: work.Value!.Id));

            Assert.True(started.IsSuccess, started.Error?.Message);
            var childPid = await WaitForPidFileAsync(childPidPath);

            var closed =
                await fixture.Sessions.CloseAsync(work.Value.Id);
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
    public async Task IndependentProcessSurvivesWorkClose()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Seconds 60"],
                WorkId: work.Value!.Id,
                Ownership: ResourceOwnership.Independent));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var closed =
            await fixture.Sessions.CloseAsync(work.Value.Id);
        Assert.True(closed.IsSuccess, closed.Error?.Message);

        var status = await fixture.Processes.StatusAsync(
            started.Value!.Handle);
        Assert.True(status.IsSuccess, status.Error?.Message);
        Assert.Equal(ManagedProcessState.Running, status.Value!.State);

        var terminated = await fixture.Processes.TerminateAsync(
            started.Value.Handle);
        Assert.True(terminated.IsSuccess, terminated.Error?.Message);

        var released = await fixture.Resources.CloseAsync(
            started.Value.Handle.AsResourceHandle());
        Assert.True(released.IsSuccess, released.Error?.Message);
    }

    [Fact]
    public async Task FullExecutablePathWithSpacesStillLaunches()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var directory =
            Directory.CreateTempSubdirectory("loom process path ");
        var executable =
            Path.Combine(directory.FullName, "copied cmd.exe");

        try
        {
            File.Copy(
                Environment.ExpandEnvironmentVariables(
                    @"%SystemRoot%\System32\cmd.exe"),
                executable);

            var started = await fixture.Processes.StartAsync(
                new ProcessStartRequest(
                    executable,
                    ["/d", "/s", "/c", "echo spaced-path-ok"],
                    WorkId: work.Value!.Id));

            Assert.True(started.IsSuccess, started.Error?.Message);
            var status = await WaitForExitAsync(
                fixture.Processes,
                started.Value!.Handle);
            Assert.Equal(0, status.ExitCode);

            var read = await WaitForOutputAsync(
                fixture.Processes,
                started.Value.Handle,
                "spaced-path-ok");
            Assert.True(read.IsSuccess, read.Error?.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailedNativeLaunchDoesNotRegisterResource()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                $"loom-missing-{Guid.NewGuid():N}.exe",
                WorkId: work.Value!.Id));

        Assert.False(started.IsSuccess);
        Assert.Equal("execution_failed", started.Error?.Code);
        Assert.Equal(0, fixture.Resources.ActiveCount);
    }

    [Fact]
    public async Task TerminateKeepsCapturedOutputReadableUntilResourceClose()
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
                    "Write-Output retained-before-terminate; " +
                    "Start-Sleep -Seconds 60"
                ],
                WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var before = await WaitForOutputAsync(
            fixture.Processes,
            started.Value!.Handle,
            "retained-before-terminate");
        Assert.True(before.IsSuccess, before.Error?.Message);

        var terminated = await fixture.Processes.TerminateAsync(
            started.Value.Handle);
        Assert.True(terminated.IsSuccess, terminated.Error?.Message);

        var after = await fixture.Processes.ReadAsync(
            started.Value.Handle,
            stdoutCursor: 0,
            stderrCursor: 0);
        Assert.True(after.IsSuccess, after.Error?.Message);
        Assert.Contains(
            "retained-before-terminate",
            string.Concat(
                after.Value!.Stdout.Chunks.Select(chunk => chunk.Text)),
            StringComparison.Ordinal);
        Assert.Equal(
            ManagedProcessState.Terminated,
            after.Value.Process.State);
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

    private static async Task<ProcessStatusResult> WaitForExitAsync(
        ProcessCapability processes,
        ProcessHandle handle)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await processes.StatusAsync(handle);
            Assert.True(status.IsSuccess, status.Error?.Message);

            if (status.Value!.State is ManagedProcessState.Exited or ManagedProcessState.Terminated)
            {
                return status.Value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Process did not exit in time.");
    }

    private static async Task<LoomLCI.Core.LoomResult<ProcessOutputReadResult>> WaitForOutputAsync(
        ProcessCapability processes,
        ProcessHandle handle,
        string expected)
    {
        LoomLCI.Core.LoomResult<ProcessOutputReadResult>? latest = null;

        for (var i = 0; i < 100; i++)
        {
            latest = await processes.ReadAsync(handle, 0, 0);
            if (!latest.IsSuccess)
            {
                return latest;
            }

            var text = string.Concat(latest.Value!.Stdout.Chunks.Select(c => c.Text));
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

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
        }
    }
}
