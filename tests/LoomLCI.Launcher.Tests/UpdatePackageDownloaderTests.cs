using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LoomLCI.Launcher;

namespace LoomLCI.Launcher.Tests;

public sealed class UpdatePackageDownloaderTests
{
    [Fact]
    public async Task SlowTransferReportsProgressAndVerifiesHash()
    {
        var data = Payload(250_000);
        var handler = new ScriptedHandler((_, _) =>
            Respond(HttpStatusCode.OK, new TestStream(
                data, delayPerRead: TimeSpan.FromMilliseconds(10))));
        var progress = new List<UpdateDownloadProgress>();

        await DownloadAsync(data, handler, p => progress.Add(p));

        Assert.Single(handler.Requests);
        Assert.Contains(progress, p => p.BytesReceived > 0 &&
                                       p.BytesReceived < data.Length);
        Assert.Equal(data.Length, progress[^1].BytesReceived);
        Assert.Equal(1, progress[^1].Attempt);
    }

    [Fact]
    public async Task InterruptedTransferResumesFromExactByteOffset()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((request, attempt) =>
        {
            if (attempt == 1)
            {
                return Respond(HttpStatusCode.OK,
                    new TestStream(data, failAt: 65_536), data.Length);
            }

            Assert.Equal(65_536, request.Headers.Range?.Ranges.Single().From);
            return Partial(data, 65_536);
        });

        await DownloadAsync(data, handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[0]);
        Assert.Equal(65_536, handler.Requests[1]);
    }

    [Fact]
    public async Task AbruptEofResumesWithRange()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((_, attempt) =>
            attempt == 1
                ? Respond(HttpStatusCode.OK,
                    new TestStream(data, endAt: 65_536), data.Length)
                : Partial(data, 65_536));

        await DownloadAsync(data, handler);
        Assert.Equal([null, 65_536L], handler.Requests);
    }

    [Fact]
    public async Task IdleTimeoutRetriesWithRange()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((_, attempt) =>
            attempt == 1
                ? Respond(HttpStatusCode.OK,
                    new TestStream(data, hangAt: 65_536), data.Length)
                : Partial(data, 65_536));

        await DownloadAsync(data, handler, options: Options() with
        {
            IdleTimeout = TimeSpan.FromMilliseconds(55)
        });

        Assert.Equal([null, 65_536L], handler.Requests);
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsInsteadOfAppending()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((_, attempt) =>
            attempt == 1
                ? Respond(HttpStatusCode.OK,
                    new TestStream(data, failAt: 65_536), data.Length)
                : Respond(HttpStatusCode.OK, new TestStream(data), data.Length));

        await DownloadAsync(data, handler);

        Assert.Equal([null, 65_536L], handler.Requests);
    }

    [Fact]
    public async Task MismatchedContentRangeFailsClosed()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((_, attempt) =>
        {
            if (attempt == 1)
            {
                return Respond(HttpStatusCode.OK,
                    new TestStream(data, failAt: 65_536), data.Length);
            }

            var incorrect = Partial(data, 65_536);
            incorrect.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(65_535, data.Length - 1, data.Length);
            return incorrect;
        });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => DownloadAsync(data, handler));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ShaMismatchIsNotRetried()
    {
        var correct = Payload(230_000);
        var tampered = correct.ToArray();
        tampered[17] ^= 0xff;
        var handler = new ScriptedHandler((_, _) =>
            Respond(HttpStatusCode.OK, new TestStream(tampered), tampered.Length));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => DownloadAsync(correct, handler));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExplicitCancellationStopsRetries()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((_, _) =>
            Respond(HttpStatusCode.OK,
                new TestStream(data, hangAt: 0), data.Length));
        using var cancel = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DownloadAsync(data, handler, token: cancel.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TotalTimeoutStopsRetries()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((_, _) =>
            Respond(HttpStatusCode.OK,
                new TestStream(data, hangAt: 0), data.Length));

        await Assert.ThrowsAsync<TimeoutException>(
            () => DownloadAsync(data, handler,
                options: Options() with
                {
                    TotalTimeout = TimeSpan.FromMilliseconds(70),
                    IdleTimeout = TimeSpan.FromSeconds(1)
                }));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RetryLimitAppliesToTemporaryServerFailures()
    {
        var data = Payload(230_000);
        var handler = new ScriptedHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => DownloadAsync(data, handler));

        Assert.Equal(3, handler.Requests.Count);
    }

    private static UpdateDownloadOptions Options() => new()
    {
        IdleTimeout = TimeSpan.FromSeconds(1),
        TotalTimeout = TimeSpan.FromSeconds(5),
        RetryDelay = TimeSpan.FromMilliseconds(1),
        ProgressInterval = TimeSpan.FromMilliseconds(1),
        MaxAttempts = 3
    };

    private static byte[] Payload(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    private static async Task DownloadAsync(
        byte[] expected,
        HttpMessageHandler handler,
        Action<UpdateDownloadProgress>? progress = null,
        UpdateDownloadOptions? options = null,
        CancellationToken token = default)
    {
        using var http = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(http, options ?? Options());
        var release = new UpdateReleaseManifest
        {
            Sequence = 4,
            Version = "v4",
            PackageUrl = "https://updates.test/package.zip",
            PackageSizeBytes = expected.Length,
            PackageSha256 = Convert.ToHexString(
                SHA256.HashData(expected)).ToLowerInvariant()
        };
        var path = Path.Combine(Path.GetTempPath(),
            "LoomLCI-DownloadTest-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await downloader.DownloadAsync(release, path, progress, token);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HttpResponseMessage Partial(byte[] data, int offset)
    {
        var remaining = data[offset..];
        var response = Respond(HttpStatusCode.PartialContent,
            new TestStream(remaining), remaining.Length);
        response.Content.Headers.ContentRange =
            new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
        return response;
    }

    private static HttpResponseMessage Respond(
        HttpStatusCode status,
        Stream body,
        int contentLength)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StreamContent(body)
        };
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    private static HttpResponseMessage Respond(
        HttpStatusCode status,
        Stream body) =>
        Respond(status, body, (int)body.Length);

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        public List<long?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers.Range?.Ranges.Single().From);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    private sealed class TestStream : Stream
    {
        private readonly byte[] _content;
        private readonly int _failAt;
        private readonly int _hangAt;
        private readonly int _endAt;
        private readonly TimeSpan _delayPerRead;
        private int _position;

        public TestStream(
            byte[] content,
            int failAt = -1,
            int hangAt = -1,
            int endAt = -1,
            TimeSpan delayPerRead = default)
        {
            _content = content;
            _failAt = failAt;
            _hangAt = hangAt;
            _endAt = endAt < 0 ? content.Length : endAt;
            _delayPerRead = delayPerRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _content.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_failAt >= 0 && _position >= _failAt)
            {
                throw new IOException("simulated network interruption");
            }

            if (_hangAt >= 0 && _position >= _hangAt)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_delayPerRead > TimeSpan.Zero)
            {
                await Task.Delay(_delayPerRead, cancellationToken);
            }

            var remaining = Math.Min(_content.Length, _endAt) - _position;
            if (_failAt >= 0)
            {
                remaining = Math.Min(remaining, _failAt - _position);
            }

            var length = Math.Min(buffer.Length, remaining);
            if (length <= 0)
            {
                return 0;
            }

            _content.AsMemory(_position, length).CopyTo(buffer);
            _position += length;
            return length;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask()
                .GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
