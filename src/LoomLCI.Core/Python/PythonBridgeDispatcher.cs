using System.Text.Json;

namespace LoomLCI.Core.Python;

public sealed class PythonBridgeDispatcher : IPythonBridgeDispatcher
{
    public const int BridgeApiVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<LoomResult<JsonElement>> DispatchAsync(
        WorkId workId,
        PythonBridgeCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(workId.Value))
        {
            return Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.InvalidArgument(
                        "work_id is required for Python bridge calls.")));
        }

        if (string.IsNullOrWhiteSpace(call.Method))
        {
            return Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.InvalidArgument(
                        "Python bridge method is required.")));
        }

        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.InvalidArgument(
                        "Python bridge arguments must be a JSON object.")));
        }

        return call.Method switch
        {
            "bridge.capabilities" => Task.FromResult(
                LoomResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(
                        Array.Empty<string>(),
                        JsonOptions))),
            _ => Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.Unsupported(
                        $"Python bridge method '{call.Method}' is not supported.")))
        };
    }
}
