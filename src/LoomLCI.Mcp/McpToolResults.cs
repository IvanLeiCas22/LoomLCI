using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace LoomLCI.Mcp;

internal static class McpToolResults
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static CallToolResult From<T>(ToolEnvelope<T> envelope)
        => From(envelope, Array.Empty<ContentBlock>());

    public static CallToolResult From<T>(
        ToolEnvelope<T> envelope,
        params ContentBlock[] successContent)
    {
        var structured = JsonSerializer.SerializeToElement(envelope, JsonOptions);
        List<ContentBlock> content =
        [
            new TextContentBlock
            {
                Text = ToTextContent(envelope)
            }
        ];

        if (envelope.Ok && successContent.Length > 0)
        {
            content.AddRange(successContent);
        }

        return new CallToolResult
        {
            IsError = !envelope.Ok,
            StructuredContent = structured,
            Content = content
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
