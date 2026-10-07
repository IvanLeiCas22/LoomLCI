using LoomLCI.Core.VisualFiles;

namespace LoomLCI.Core.Tests;

public sealed class VisualImageFormatTests
{
    [Fact]
    public void DetectsSupportedContainers()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPkqrjDwMDAxMDAwMDAAAAPwAFizZEe6AAAAABJRU5ErkJggg==");
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x02,
            0xFF, 0xDA, 0x00, 0x02,
            0xFF, 0xD9
        ];
        byte[] webp =
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            12, 0, 0, 0,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P',
            (byte)'V', (byte)'P', (byte)'8', (byte)' ',
            0, 0, 0, 0
        ];

        Assert.Equal("image/png", VisualImageFormat.DetectMimeType(png));
        Assert.Equal("image/jpeg", VisualImageFormat.DetectMimeType(jpeg));
        Assert.Equal("image/webp", VisualImageFormat.DetectMimeType(webp));
    }

    [Theory]
    [InlineData("iVBORw0KGgoBAgME")]
    [InlineData("/9j/AQIDBP/Z")]
    [InlineData("UklGRnh4eHhXRUJQeHh4eA==")]
    public void RejectsInvalidOrTruncatedContainers(string base64)
    {
        Assert.Null(
            VisualImageFormat.DetectMimeType(
                Convert.FromBase64String(base64)));
    }
}
