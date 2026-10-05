using LoomLCI.PdfWorker;

namespace LoomLCI.PdfWorker.Tests;

public sealed class PdfWorkerTextLimitTests
{
    [Fact]
    public void AstralUnicodeCountsAsOneCodePoint()
    {
        var result = PdfWorkerProgram.TruncateByCodePoints("A😀B", 3);

        Assert.Equal("A😀B", result.Text);
        Assert.Equal(3, result.CodePoints);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void TruncationNeverSplitsSurrogatePair()
    {
        var result = PdfWorkerProgram.TruncateByCodePoints("A😀BC", 2);

        Assert.Equal("A😀", result.Text);
        Assert.Equal(2, result.CodePoints);
        Assert.True(result.Truncated);
        Assert.False(char.IsHighSurrogate(result.Text[^1]));
    }

    [Fact]
    public void ExactLimitIsNotReportedAsTruncated()
    {
        var result = PdfWorkerProgram.TruncateByCodePoints("abcd", 4);

        Assert.Equal("abcd", result.Text);
        Assert.Equal(4, result.CodePoints);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void TextBeyondLimitIsReportedAsTruncated()
    {
        var result = PdfWorkerProgram.TruncateByCodePoints("abcde", 4);

        Assert.Equal("abcd", result.Text);
        Assert.Equal(4, result.CodePoints);
        Assert.True(result.Truncated);
    }
}
