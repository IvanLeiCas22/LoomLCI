using System.Text.Json;
using LoomLCI.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LoomLCI.Mcp.Tests;

public sealed class McpToolResultsTests
{
    [Fact]
    public void MixedImageResultRoundTrips()
    {
        var bytes = new byte[900_000];
        new Random(12345).NextBytes(bytes);
        var envelope = new ToolEnvelope<int>(true, bytes.Length, null);

        var result = McpToolResults.From(
            envelope,
            ImageContentBlock.FromBytes(bytes, "image/png"));

        var json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        var roundTrip = JsonSerializer.Deserialize<CallToolResult>(
            json,
            McpJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTrip);
        Assert.False(roundTrip.IsError ?? false);
        Assert.True(roundTrip.StructuredContent.HasValue);
        Assert.Equal(2, roundTrip.Content.Count);
        Assert.IsType<TextContentBlock>(roundTrip.Content[0]);

        var image = Assert.IsType<ImageContentBlock>(roundTrip.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.DecodedData.Span.SequenceEqual(bytes));
    }

    [Fact]
    public void ImageSizeEstimatorMatchesActualSerializationIncludingPlusEscapes()
    {
        var bytes = CreatePlusHeavyBytes(65_537);
        var envelope = new ToolEnvelope<int>(true, bytes.Length, null);
        var result = McpToolResults.From(
            envelope,
            ImageContentBlock.FromBytes(bytes, "image/png"));

        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(
            result,
            McpJsonUtilities.DefaultOptions).LongLength;
        var estimatedBytes = McpVisualPayloadLimits.EstimateSerializedImageResultBytes(
            envelope,
            bytes,
            "image/png");

        Assert.Equal(actualBytes, estimatedBytes);
    }

    [Fact]
    public void ErrorResultDoesNotIncludeSuccessImageContent()
    {
        var envelope = new ToolEnvelope<int>(
            false,
            default,
            new ToolError("unsupported", "No image.", false, null));

        var result = McpToolResults.From(
            envelope,
            ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png"));

        Assert.True(result.IsError);
        Assert.Single(result.Content);
        Assert.IsType<TextContentBlock>(result.Content[0]);
    }

    [Fact]
    public void VisualPayloadLimitsEnforceBinaryAndSerializedCaps()
    {
        var randomBytes = new byte[McpVisualPayloadLimits.MaxImageBytes];
        new Random(12345).NextBytes(randomBytes);
        var randomEnvelope = new ToolEnvelope<int>(true, randomBytes.Length, null);

        Assert.True(McpVisualPayloadLimits.IsWithinLimits(
            randomEnvelope,
            randomBytes,
            "image/png",
            out var randomEstimatedBytes));
        Assert.InRange(
            randomEstimatedBytes,
            1,
            McpVisualPayloadLimits.MaxVisualCallToolResultBytes);

        var oversizedBytes = new byte[McpVisualPayloadLimits.MaxImageBytes + 1];
        var oversizedEnvelope = new ToolEnvelope<int>(true, oversizedBytes.Length, null);

        Assert.False(McpVisualPayloadLimits.IsWithinLimits(
            oversizedEnvelope,
            oversizedBytes,
            "image/png",
            out var oversizedEstimatedBytes));
        Assert.Equal(0, oversizedEstimatedBytes);

        var plusHeavyBytes = CreatePlusHeavyBytes(
            McpVisualPayloadLimits.MaxImageBytes);
        var plusHeavyEnvelope = new ToolEnvelope<int>(
            true,
            plusHeavyBytes.Length,
            null);

        Assert.False(McpVisualPayloadLimits.IsWithinLimits(
            plusHeavyEnvelope,
            plusHeavyBytes,
            "image/png",
            out var plusHeavyEstimatedBytes));
        Assert.True(
            plusHeavyEstimatedBytes >
            McpVisualPayloadLimits.MaxVisualCallToolResultBytes);
    }

    [Fact]
    public void GenericPayloadMeasurementUsesSharedSerializedBudget()
    {
        var envelope = new ToolEnvelope<string>(
            true,
            new string('\\', 1024),
            null);
        var result = McpToolResults.From(envelope);

        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(
            result,
            McpJsonUtilities.DefaultOptions).LongLength;

        Assert.Equal(
            actualBytes,
            McpPayloadLimits.MeasureSerializedCallToolResultBytes(result));
        Assert.Equal(
            McpPayloadLimits.MaxCallToolResultBytes,
            McpVisualPayloadLimits.MaxVisualCallToolResultBytes);
    }

    [Fact]
    public void GenericPayloadMeasurementDistinguishesBelowAndAboveBudget()
    {
        var below = McpToolResults.From(
            new ToolEnvelope<string>(
                true,
                new string('x', McpPayloadLimits.MaxCallToolResultBytes - 4096),
                null));
        var above = McpToolResults.From(
            new ToolEnvelope<string>(
                true,
                new string('x', McpPayloadLimits.MaxCallToolResultBytes),
                null));

        Assert.True(
            McpPayloadLimits.MeasureSerializedCallToolResultBytes(below) <=
            McpPayloadLimits.MaxCallToolResultBytes);
        Assert.True(
            McpPayloadLimits.MeasureSerializedCallToolResultBytes(above) >
            McpPayloadLimits.MaxCallToolResultBytes);
    }

    private static byte[] CreatePlusHeavyBytes(int size)
    {
        var bytes = new byte[size];

        for (var index = 0; index + 2 < bytes.Length; index += 3)
        {
            bytes[index] = 0xFB;
            bytes[index + 1] = 0xEF;
            bytes[index + 2] = 0xBE;
        }

        return bytes;
    }
}
