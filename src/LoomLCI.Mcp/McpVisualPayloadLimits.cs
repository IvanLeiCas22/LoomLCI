using System.Text.Json;
using LoomLCI.Core.VisualFiles;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LoomLCI.Mcp;

internal static class McpVisualPayloadLimits
{
    public const int MaxImageBytes = VisualFilesLimits.MaxImageBytes;
    public const int MaxVisualCallToolResultBytes = 9 * 1024 * 1024;

    public static bool IsWithinLimits<T>(
        ToolEnvelope<T> envelope,
        ReadOnlyMemory<byte> imageBytes,
        string mimeType,
        out long estimatedCallToolResultBytes)
    {
        if (imageBytes.Length > MaxImageBytes)
        {
            estimatedCallToolResultBytes = 0;
            return false;
        }

        estimatedCallToolResultBytes = EstimateSerializedImageResultBytes(
            envelope,
            imageBytes,
            mimeType);

        return estimatedCallToolResultBytes <= MaxVisualCallToolResultBytes;
    }

    public static long EstimateSerializedImageResultBytes<T>(
        ToolEnvelope<T> envelope,
        ReadOnlyMemory<byte> imageBytes,
        string mimeType)
    {
        if (!envelope.Ok)
        {
            throw new ArgumentException(
                "Visual payload estimation requires a successful tool envelope.",
                nameof(envelope));
        }

        var emptyImageResult = McpToolResults.From(
            envelope,
            ImageContentBlock.FromBytes(ReadOnlyMemory<byte>.Empty, mimeType));

        var baseSize = JsonSerializer.SerializeToUtf8Bytes(
            emptyImageResult,
            McpJsonUtilities.DefaultOptions).LongLength;

        return checked(baseSize + EscapedBase64JsonLength(imageBytes.Span));
    }

    private static long EscapedBase64JsonLength(ReadOnlySpan<byte> bytes)
    {
        var base64Length = 4L * ((bytes.Length + 2L) / 3L);
        long plusCount = 0;
        var index = 0;

        for (; index + 2 < bytes.Length; index += 3)
        {
            var b0 = bytes[index];
            var b1 = bytes[index + 1];
            var b2 = bytes[index + 2];

            if ((b0 >> 2) == 62)
            {
                plusCount++;
            }

            if ((((b0 & 0x03) << 4) | (b1 >> 4)) == 62)
            {
                plusCount++;
            }

            if ((((b1 & 0x0F) << 2) | (b2 >> 6)) == 62)
            {
                plusCount++;
            }

            if ((b2 & 0x3F) == 62)
            {
                plusCount++;
            }
        }

        var remaining = bytes.Length - index;
        if (remaining == 1)
        {
            var b0 = bytes[index];

            if ((b0 >> 2) == 62)
            {
                plusCount++;
            }

            if (((b0 & 0x03) << 4) == 62)
            {
                plusCount++;
            }
        }
        else if (remaining == 2)
        {
            var b0 = bytes[index];
            var b1 = bytes[index + 1];

            if ((b0 >> 2) == 62)
            {
                plusCount++;
            }

            if ((((b0 & 0x03) << 4) | (b1 >> 4)) == 62)
            {
                plusCount++;
            }

            if (((b1 & 0x0F) << 2) == 62)
            {
                plusCount++;
            }
        }

        return checked(base64Length + (5L * plusCount));
    }
}
