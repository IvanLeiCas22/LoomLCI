using LoomLCI.Core.Observability;

namespace LoomLCI.Core.Tests;

public sealed class DiagnosticEventProjectorTests
{
    [Fact]
    public void WhitelistOmitsPathsMessagesAndArbitraryPayload()
    {
        var secret = "SECRET_NOT_FOR_DISK";
        var evt = new LoomEvent("evt_1", DateTimeOffset.UtcNow,
            "DirectoryCreated", "filesystem",
            Payload: new Dictionary<string, object?>
            {
                ["path"] = "C:\\secret-" + secret,
                ["message"] = secret,
                ["token"] = secret
            });
        var projected = DiagnosticEventProjector.Project(evt);
        Assert.NotNull(projected);
        var json = System.Text.Json.JsonSerializer.Serialize(projected);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UntrustedSymbolsAreDiscarded()
    {
        var evt = new LoomEvent("evt_2", DateTimeOffset.UtcNow,
            "InvocationCompleted", "core.invocation",
            Payload: new Dictionary<string, object?>
            {
                ["operation"] = "python.execute\\secret",
                ["success"] = false,
                ["error"] = "password=value"
            });
        var projected = DiagnosticEventProjector.Project(evt)!;
        Assert.Null(projected.Operation);
        Assert.Null(projected.Code);
        Assert.Equal("failure", projected.Outcome);
    }

    [Fact]
    public void ExceptionEventDoesNotCaptureTracebacks()
    {
        var evt = new LoomEvent("evt_3", DateTimeOffset.UtcNow,
            "PythonExecutionException", "python",
            Payload: new Dictionary<string, object?>
            {
                ["traceback"] = "PRIVATE_TRACEBACK",
                ["code"] = "secret"
            });
        var projected = DiagnosticEventProjector.Project(evt)!;
        var json = System.Text.Json.JsonSerializer.Serialize(projected);
        Assert.DoesNotContain("PRIVATE_TRACEBACK", json);
        Assert.Null(projected.Code);
    }

    [Fact]
    public void ObserverDoesNotConsumeRegularChannelAndExceptionsAreIsolated()
    {
        var events = new LoomEventBus();
        var count = 0;
        events.SetDiagnosticsObserver(_ => count++);
        Assert.True(events.Publish("WorkSessionCreated", "core.work"));
        Assert.True(events.TryRead(out var regular));
        Assert.Equal("WorkSessionCreated", regular!.Kind);
        Assert.Equal(1, count);
        events.SetDiagnosticsObserver(_ => throw new IOException("fault"));
        Assert.True(events.Publish("WorkSessionClosed", "core.work"));
        Assert.True(events.TryRead(out var second));
        Assert.Equal("WorkSessionClosed", second!.Kind);
    }
}
