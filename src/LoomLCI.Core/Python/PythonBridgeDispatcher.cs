using System.Text.Json;

namespace LoomLCI.Core.Python;

public sealed class PythonBridgeDispatcher : IPythonBridgeDispatcher
{
    public const int BridgeApiVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyDictionary<string, IPythonBridgeModule> _modulesByMethod;
    private readonly string[] _capabilities;

    public PythonBridgeDispatcher()
        : this(Array.Empty<IPythonBridgeModule>())
    {
    }

    public PythonBridgeDispatcher(IEnumerable<IPythonBridgeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var byMethod = new Dictionary<string, IPythonBridgeModule>(
            StringComparer.Ordinal);

        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);

            foreach (var method in module.Methods)
            {
                if (string.IsNullOrWhiteSpace(method) ||
                    method.Length > PythonBridgeLimits.MaxMethodChars)
                {
                    throw new InvalidOperationException(
                        "Python bridge modules must expose non-empty bounded method names.");
                }

                if (!byMethod.TryAdd(method, module))
                {
                    throw new InvalidOperationException(
                        $"Python bridge method '{method}' is registered more than once.");
                }
            }
        }

        _modulesByMethod = byMethod;
        _capabilities = byMethod.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

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

        if (string.IsNullOrWhiteSpace(call.Method) ||
            call.Method.Length > PythonBridgeLimits.MaxMethodChars)
        {
            return Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.InvalidArgument(
                        "Python bridge method must be a non-empty bounded string.")));
        }

        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.InvalidArgument(
                        "Python bridge arguments must be a JSON object.")));
        }

        if (string.Equals(
                call.Method,
                "bridge.capabilities",
                StringComparison.Ordinal))
        {
            if (call.Arguments.EnumerateObject().Any())
            {
                return Task.FromResult(
                    LoomResult<JsonElement>.Failure(
                        LoomErrors.InvalidArgument(
                            "bridge.capabilities does not accept arguments.")));
            }

            return Task.FromResult(
                LoomResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(
                        _capabilities,
                        JsonOptions)));
        }

        if (!_modulesByMethod.TryGetValue(
                call.Method,
                out var module))
        {
            return Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.Unsupported(
                        $"Python bridge method '{call.Method}' is not supported.")));
        }

        return module.DispatchAsync(
            workId,
            call,
            cancellationToken);
    }
}
