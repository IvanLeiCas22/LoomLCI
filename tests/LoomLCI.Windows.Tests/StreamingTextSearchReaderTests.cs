using System.Text;
using LoomLCI.Windows.Filesystem;

namespace LoomLCI.Windows.Tests;

public sealed class StreamingTextSearchReaderTests
{
    [Fact]
    public async Task TracksExactLineOffsetsAcrossSupportedEncodings()
    {
        var cases = new (Encoding Encoding, string Id)[]
        {
            (new UTF8Encoding(true), "utf-8"),
            (new UnicodeEncoding(false, true), "utf-16le"),
            (new UnicodeEncoding(true, true), "utf-16be"),
            (new UTF32Encoding(false, true), "utf-32le"),
            (new UTF32Encoding(true, true), "utf-32be")
        };

        foreach (var (encoding, expectedId) in cases)
        {
            var path = CreateTempPath();
            try
            {
                const string first = "uno\r\n";
                const string second = "αneedle😀\n";
                const string third = "omega";
                await File.WriteAllTextAsync(path, first + second + third, encoding);

                var format = await StreamingTextSearchReader.InspectAsync(
                    path,
                    CancellationToken.None);
                Assert.Equal(expectedId, format.EncodingId);
                Assert.False(format.IsBinary);

                await using var reader = await StreamingTextSearchReader.OpenAsync(
                    path,
                    format,
                    ["needle"],
                    caseSensitive: true,
                    startLine: 1,
                    startByteOffset: format.PreambleLength,
                    CancellationToken.None);

                var line1 = await reader.ReadLineAsync(CancellationToken.None);
                var line2 = await reader.ReadLineAsync(CancellationToken.None);
                var line3 = await reader.ReadLineAsync(CancellationToken.None);
                Assert.NotNull(line1);
                Assert.NotNull(line2);
                Assert.NotNull(line3);
                Assert.Null(await reader.ReadLineAsync(CancellationToken.None));

                Assert.Equal(1, line1.LineNumber);
                Assert.Equal(format.PreambleLength, line1.StartByteOffset);
                Assert.Equal(
                    format.PreambleLength + encoding.GetByteCount(first),
                    line1.NextByteOffset);

                Assert.Equal(2, line2.LineNumber);
                Assert.Equal(line1.NextByteOffset, line2.StartByteOffset);
                Assert.Equal(
                    line2.StartByteOffset + encoding.GetByteCount(second),
                    line2.NextByteOffset);
                Assert.Equal(
                    2,
                    Assert.Single(line2.QueryMatches).Column);

                Assert.Equal(3, line3.LineNumber);
                Assert.Equal(line2.NextByteOffset, line3.StartByteOffset);
                Assert.Equal(new FileInfo(path).Length, line3.NextByteOffset);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task HandlesCrLfLfAndCrAsPhysicalLineSeparators()
    {
        var path = CreateTempPath();
        try
        {
            await File.WriteAllTextAsync(
                path,
                "one\r\ntwo\nthree\rfour",
                new UTF8Encoding(false));

            var format = await StreamingTextSearchReader.InspectAsync(
                path,
                CancellationToken.None);
            await using var reader = await StreamingTextSearchReader.OpenAsync(
                path,
                format,
                ["o"],
                caseSensitive: true,
                startLine: 1,
                startByteOffset: format.PreambleLength,
                CancellationToken.None);

            var lines = new List<ScannedTextLine>();
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lines.Add(line);
            }

            Assert.Equal(4, lines.Count);
            Assert.Equal([1, 2, 3, 4], lines.Select(line => line.LineNumber));
            Assert.Equal(
                new FileInfo(path).Length,
                lines[^1].NextByteOffset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindsQueryAcrossReaderBufferBoundary()
    {
        var path = CreateTempPath();
        try
        {
            var text = new string('x', 8190) + "needle" + new string('y', 100);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));

            var format = await StreamingTextSearchReader.InspectAsync(
                path,
                CancellationToken.None);
            await using var reader = await StreamingTextSearchReader.OpenAsync(
                path,
                format,
                ["needle"],
                caseSensitive: true,
                startLine: 1,
                startByteOffset: format.PreambleLength,
                CancellationToken.None);

            var line = await reader.ReadLineAsync(CancellationToken.None);
            Assert.NotNull(line);
            var match = Assert.Single(line.QueryMatches);
            Assert.Equal(8191, match.Column);
            Assert.Contains("needle", line.Excerpt.Text, StringComparison.Ordinal);
            Assert.True(line.Excerpt.Truncated);
            Assert.True(line.Excerpt.StartColumn > 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GiantSingleLineProducesBoundedExcerpt()
    {
        var path = CreateTempPath();
        try
        {
            const int prefixLength = 2 * 1024 * 1024;
            var text = new string('x', prefixLength) + "TARGET" + new string('z', 4096);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));

            var format = await StreamingTextSearchReader.InspectAsync(
                path,
                CancellationToken.None);
            await using var reader = await StreamingTextSearchReader.OpenAsync(
                path,
                format,
                ["TARGET"],
                caseSensitive: true,
                startLine: 1,
                startByteOffset: format.PreambleLength,
                CancellationToken.None);

            var line = await reader.ReadLineAsync(CancellationToken.None);
            Assert.NotNull(line);
            Assert.Equal(prefixLength + 1, Assert.Single(line.QueryMatches).Column);
            Assert.True(line.Excerpt.Text.Length <= StreamingTextSearchReader.MaxExcerptChars);
            Assert.Contains("TARGET", line.Excerpt.Text, StringComparison.Ordinal);
            Assert.True(line.Excerpt.Truncated);
            Assert.True(line.Excerpt.StartColumn > 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BinaryPrefixDetectionDoesNotMisclassifyUtf16Text()
    {
        var utf16 = CreateTempPath();
        var binary = CreateTempPath();
        try
        {
            await File.WriteAllTextAsync(
                utf16,
                "alpha needle",
                new UnicodeEncoding(false, true));
            await File.WriteAllBytesAsync(
                binary,
                Encoding.UTF8.GetBytes("alpha\0needle"));

            var textFormat = await StreamingTextSearchReader.InspectAsync(
                utf16,
                CancellationToken.None);
            var binaryFormat = await StreamingTextSearchReader.InspectAsync(
                binary,
                CancellationToken.None);

            Assert.False(textFormat.IsBinary);
            Assert.True(binaryFormat.IsBinary);
        }
        finally
        {
            File.Delete(utf16);
            File.Delete(binary);
        }
    }

    private static string CreateTempPath()
        => Path.Combine(
            Path.GetTempPath(),
            $"loomlci-search-{Guid.NewGuid():N}.txt");
}
