using LoomLCI.Core.Filesystem;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.VisualFiles;
using LoomLCI.Core.Work;
using LoomLCI.Windows.VisualFiles;

namespace LoomLCI.Windows.Tests;

public sealed class VisualFilesCapabilityTests
{
    public static IEnumerable<object[]> SupportedImages()
    {
        yield return ["sample.png", "image/png", Decode(PngBase64)];
        yield return ["sample.jpg", "image/jpeg", Decode(JpegBase64)];
        yield return ["progressive.jpg", "image/jpeg", Decode(ProgressiveJpegBase64)];
        yield return ["sample.webp", "image/webp", Decode(WebPBase64)];
        yield return ["lossless.webp", "image/webp", Decode(WebPLosslessBase64)];
        yield return ["alpha.webp", "image/webp", Decode(WebPAlphaBase64)];
        yield return ["renamed.bin", "image/png", Decode(PngBase64)];
    }

    [Theory]
    [MemberData(nameof(SupportedImages))]
    public async Task SupportedImagesAreDetectedByContentAndPreserved(
        string fileName,
        string expectedMimeType,
        byte[] bytes)
    {
        await using var fixture = new VisualFilesFixture();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, fileName), bytes);
        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var result = await fixture.VisualFiles.ViewImageAsync(fileName, work.Value!.Id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(fileName, result.Value!.RequestedPath);
        Assert.Equal(Path.Combine(fixture.Root, fileName), result.Value.FullPath);
        Assert.Equal(expectedMimeType, result.Value.MimeType);
        Assert.Equal(bytes, result.Value.Bytes);
    }

    [Fact]
    public async Task AbsolutePathWorksWithoutWorkSession()
    {
        await using var fixture = new VisualFilesFixture();
        var path = Path.Combine(fixture.Root, "absolute.png");
        var bytes = Decode(PngBase64);
        await File.WriteAllBytesAsync(path, bytes);

        var result = await fixture.VisualFiles.ViewImageAsync(path);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(path, result.Value!.RequestedPath);
        Assert.Equal(path, result.Value.FullPath);
        Assert.Equal(bytes, result.Value.Bytes);
    }

    [Fact]
    public async Task RelativePathWithoutWorkSessionIsRejected()
    {
        await using var fixture = new VisualFilesFixture();

        var result = await fixture.VisualFiles.ViewImageAsync("image.png");

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
    }

    [Theory]
    [InlineData("fake.png", "iVBORw0KGgoBAgME")]
    [InlineData("fake.jpg", "/9j/AQIDBP/Z")]
    [InlineData("fake.webp", "UklGRnh4eHhXRUJQeHh4eA==")]
    public async Task InvalidOrTruncatedContainersAreRejected(
        string fileName,
        string base64)
    {
        await using var fixture = new VisualFilesFixture();
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Root, fileName),
            Decode(base64));
        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var result = await fixture.VisualFiles.ViewImageAsync(fileName, work.Value!.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal(
            "unsupported_image_format",
            result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task OversizedImageIsRejectedBeforePayloadCreation()
    {
        await using var fixture = new VisualFilesFixture();
        var path = Path.Combine(fixture.Root, "large.png");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(VisualFilesLimits.MaxImageBytes + 1L);
        }

        var result = await fixture.VisualFiles.ViewImageAsync(path);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("image_too_large", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task ActiveWriterReturnsRetryableBusy()
    {
        await using var fixture = new VisualFilesFixture();
        var path = Path.Combine(fixture.Root, "busy.png");
        await File.WriteAllBytesAsync(path, Decode(PngBase64));

        await using var writer = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        var result = await fixture.VisualFiles.ViewImageAsync(path);

        Assert.False(result.IsSuccess);
        Assert.Equal("busy", result.Error?.Code);
        Assert.True(result.Error?.Retryable);
        Assert.Equal("file_busy", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task MissingImageReturnsNotFound()
    {
        await using var fixture = new VisualFilesFixture();
        var path = Path.Combine(fixture.Root, "missing.png");

        var result = await fixture.VisualFiles.ViewImageAsync(path);

        Assert.False(result.IsSuccess);
        Assert.Equal("not_found", result.Error?.Code);
    }

    private static byte[] Decode(string base64) => Convert.FromBase64String(base64);

    private sealed class VisualFilesFixture : IAsyncDisposable
    {
        public VisualFilesFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "LoomLCI.VisualFiles.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(Resources, Events);
            Invocations = new InvocationRunner(Events, Sessions);
            VisualFiles = new VisualFilesCapability(
                new WindowsVisualFilesProvider(),
                Invocations);
        }

        public string Root { get; }
        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public VisualFilesCapability VisualFiles { get; }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPkqrjDwMDAxMDAwMDAAAAPwAFizZEe6AAAAABJRU5ErkJggg==";

    private const string JpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDCooor70+HP//Z";

    private const string ProgressiveJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wgARCAACAAIDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAT/xAAUAQEAAAAAAAAAAAAAAAAAAAAF/9oADAMBAAIQAxAAAAGAPB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAEFAn//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/AX//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/AX//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/An//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/IX//2gAMAwEAAgADAAAAEPv/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EH//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EH//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EH//2Q==";

    private const string WebPBase64 =
        "UklGRjwAAABXRUJQVlA4IDAAAAAQAgCdASoCAAIAAUAmJaACdLoB+AH4AAPIAP7udn/+yr4TI/9qv/7GYgrmvv5AAAA=";

    private const string WebPLosslessBase64 =
        "UklGRh4AAABXRUJQVlA4TBEAAAAvAUAAAAdQvCqUu/+BiOh/AAA=";

    private const string WebPAlphaBase64 =
        "UklGRlwAAABXRUJQVlA4WAoAAAAQAAAAAQAAAQAAQUxQSAUAAAAAZGRkZABWUDggMAAAABACAJ0BKgIAAgABQCYloAJ0ugH4AfgAA8gA/u52f/7KvhMj/2q//sZiCua+/kAAAA==";
}
