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
}
