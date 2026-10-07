using System.Text.Json;
using LoomLCI.Core;
using LoomLCI.Core.Python;

namespace LoomLCI.Core.Tests;

public sealed class PythonBridgeDispatcherTests
{
    [Fact]
    public async Task CapabilitiesReturnsEmptyFoundationSurface()
    {
        var dispatcher = new PythonBridgeDispatcher();

        var result = await dispatcher.DispatchAsync(
            WorkId.Create(),
            new PythonBridgeCall(
                "bridge.capabilities",
                JsonSerializer.SerializeToElement(
                    new { })),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            JsonValueKind.Array,
            result.Value.ValueKind);
        Assert.Equal(
            0,
            result.Value.GetArrayLength());
    }

    [Fact]
    public async Task CapabilitiesAggregateModuleMethodsInStableOrder()
    {
        var dispatcher = new PythonBridgeDispatcher(
        [
            new FakeModule(
                ["fs.search_text", "fs.list_tree"])
        ]);

        var result = await dispatcher.DispatchAsync(
            WorkId.Create(),
            new PythonBridgeCall(
                "bridge.capabilities",
                JsonSerializer.SerializeToElement(
                    new { })),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            ["fs.list_tree", "fs.search_text"],
            result.Value
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    [Fact]
    public void DuplicateModuleMethodRegistrationIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new PythonBridgeDispatcher(
            [
                new FakeModule(["fs.list_tree"]),
                new FakeModule(["fs.list_tree"])
            ]));

        Assert.Contains(
            "registered more than once",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredMethodRoutesToOwningModule()
    {
        var module = new FakeModule(["fs.list_tree"]);
        var dispatcher = new PythonBridgeDispatcher([module]);

        var result = await dispatcher.DispatchAsync(
            WorkId.Create(),
            new PythonBridgeCall(
                "fs.list_tree",
                JsonSerializer.SerializeToElement(
                    new { path = "." })),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            "fs.list_tree",
            result.Value.GetProperty("method").GetString());
        Assert.Equal(1, module.CallCount);
    }

    [Fact]
    public async Task UnknownMethodReturnsRecoverableUnsupportedError()
    {
        var dispatcher = new PythonBridgeDispatcher();

        var result = await dispatcher.DispatchAsync(
            WorkId.Create(),
            new PythonBridgeCall(
                "missing.method",
                JsonSerializer.SerializeToElement(
                    new { })),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "unsupported",
            result.Error?.Code);
        Assert.False(
            result.Error?.Retryable ?? true);
    }

    [Fact]
    public async Task ArgumentsMustBeObject()
    {
        var dispatcher = new PythonBridgeDispatcher();

        var result = await dispatcher.DispatchAsync(
            WorkId.Create(),
            new PythonBridgeCall(
                "bridge.capabilities",
                JsonSerializer.SerializeToElement(
                    Array.Empty<string>())),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "invalid_argument",
            result.Error?.Code);
    }

    private sealed class FakeModule(
        IReadOnlyList<string> methods)
        : IPythonBridgeModule
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<string> Methods { get; } = methods;

        public Task<LoomResult<JsonElement>> DispatchAsync(
            WorkId workId,
            PythonBridgeCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            return Task.FromResult(
                LoomResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            method = call.Method
                        })));
        }
    }
}
