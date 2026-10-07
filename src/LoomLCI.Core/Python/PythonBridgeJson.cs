using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoomLCI.Core.Python;

internal static class PythonBridgeJson
{
    private static readonly JsonSerializerOptions InputOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private static readonly JsonSerializerOptions ResultOptions =
        CreateResultOptions();

    public static LoomResult<T> Deserialize<T>(
        JsonElement arguments,
        string method)
        where T : class
    {
        try
        {
            var value = arguments.Deserialize<T>(InputOptions);

            return value is not null
                ? LoomResult<T>.Success(value)
                : LoomResult<T>.Failure(
                    LoomErrors.InvalidArgument(
                        $"{method} arguments are invalid."));
        }
        catch (JsonException ex)
        {
            return LoomResult<T>.Failure(
                LoomErrors.InvalidArgument(
                    $"{method} arguments are invalid: {ex.Message}"));
        }
        catch (NotSupportedException ex)
        {
            return LoomResult<T>.Failure(
                LoomErrors.InvalidArgument(
                    $"{method} arguments are invalid: {ex.Message}"));
        }
    }

    public static LoomResult<JsonElement> MapResult<T>(
        string method,
        LoomResult<T> result,
        string? sizeHint = null)
    {
        if (!result.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                result.Error!);
        }

        return MapValue(
            method,
            result.Value,
            sizeHint);
    }

    public static LoomResult<JsonElement> MapValue<T>(
        string method,
        T value,
        string? sizeHint = null)
    {
        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(
                value,
                ResultOptions);
        }
        catch (Exception ex) when (
            ex is JsonException or
            NotSupportedException)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.Internal(
                    $"Could not serialize Python bridge result: {ex.Message}"));
        }

        var serializedBytes = Encoding.UTF8.GetByteCount(
            element.GetRawText());

        if (serializedBytes >
            PythonBridgeLimits.MaxResultFrameBytes)
        {
            var message =
                $"Python bridge result for '{method}' exceeds " +
                $"{PythonBridgeLimits.MaxResultFrameBytes} bytes.";

            if (!string.IsNullOrWhiteSpace(sizeHint))
            {
                message += $" {sizeHint}";
            }

            return LoomResult<JsonElement>.Failure(
                new LoomError(
                    "unsupported",
                    message,
                    false,
                    new Dictionary<string, object?>
                    {
                        ["reason"] = "bridge_payload_too_large",
                        ["method"] = method,
                        ["serialized_result_bytes"] =
                            serializedBytes,
                        ["max_bridge_result_bytes"] =
                            PythonBridgeLimits.MaxResultFrameBytes
                    }));
        }

        return LoomResult<JsonElement>.Success(element);
    }

    private static JsonSerializerOptions CreateResultOptions()
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.SnakeCaseLower
        };

        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower));

        return options;
    }
}
