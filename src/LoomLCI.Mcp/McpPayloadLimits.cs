using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LoomLCI.Mcp;

internal static class McpPayloadLimits
{
    public const int MaxCallToolResultBytes = 9 * 1024 * 1024;

    public static long MeasureSerializedCallToolResultBytes(CallToolResult result)
        => JsonSerializer.SerializeToUtf8Bytes(
            result,
            McpJsonUtilities.DefaultOptions).LongLength;
}
