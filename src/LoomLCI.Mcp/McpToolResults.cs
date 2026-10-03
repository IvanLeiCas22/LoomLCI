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
                    Text = structured.GetRawText()
                }
            ]
        };
    }
}
