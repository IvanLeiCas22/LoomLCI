using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace LoomLCI.Mcp;

internal static class McpToolResults
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static CallToolResult From<T>(ToolEnvelope<T> envelope)
    {
        var structured = JsonSerializer.SerializeToElement(envelope, JsonOptions);

        return new CallToolResult
        {
            IsError = !envelope.Ok,
            StructuredContent = structured,
            Content =
            [
                new TextContentBlock
                {
                    Text = ToTextContent(envelope)
                }
            ]
        };
    }

    private static string ToTextContent<T>(ToolEnvelope<T> envelope)
    {
        if (envelope.Ok)
        {
            return "Tool completed successfully. Structured result attached.";
        }

        return envelope.Error is { } error
            ? $"{error.Code}: {error.Message}"
            : "Tool failed.";
    }
}
