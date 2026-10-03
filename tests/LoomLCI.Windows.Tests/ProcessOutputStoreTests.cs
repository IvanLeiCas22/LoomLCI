using LoomLCI.Windows.Processes;

namespace LoomLCI.Windows.Tests;

public sealed class ProcessOutputStoreTests
{
    [Fact]
    public void SmallReadsAdvanceByExactCharacterPositionWithoutSkipping()
    {
        using var store = new ProcessOutputStore(1024);
        store.Append("abcdef");

        var cursor = 0L;
        var output = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var read = store.Read(cursor, 1);
            var chunk = Assert.Single(read.Chunks);
            output.Add(chunk.Text);
            Assert.Equal(cursor, chunk.Cursor);
            Assert.Equal(cursor + 1, read.NextCursor);
            Assert.Equal(6, read.RetainedUntilCursor);
            Assert.Equal(6, read.ObservedUntilCursor);
            Assert.False(read.RetentionLimitReached);
            cursor = read.NextCursor;
        }

        Assert.Equal("abcdef", string.Concat(output));
        Assert.Empty(store.Read(cursor, 1).Chunks);
    }

    [Fact]
    public void ReadDoesNotSplitUtf16SurrogatePair()
    {
        using var store = new ProcessOutputStore(1024);
        store.Append("😀X");

        var first = store.Read(0, 1);
        Assert.Equal("😀", Assert.Single(first.Chunks).Text);
        Assert.Equal(2, first.NextCursor);

        var second = store.Read(first.NextCursor, 1);
        Assert.Equal("X", Assert.Single(second.Chunks).Text);
        Assert.Equal(3, second.NextCursor);
    }

    [Fact]
    public void RetentionLimitDoesNotKeepHalfSurrogatePair()
    {
        using var store = new ProcessOutputStore(1);
        store.Append("😀X");

        var read = store.Read(0, 10);
        Assert.Empty(read.Chunks);
        Assert.Equal(0, read.RetainedUntilCursor);
        Assert.Equal(3, read.ObservedUntilCursor);
        Assert.True(read.RetentionLimitReached);
    }

    [Fact]
    public void RetentionLimitKeepsPrefixAndReportsExactOmittedRange()
    {
        using var store = new ProcessOutputStore(4);
        store.Append("abcdef");

        var read = store.Read(0, 10);

        Assert.Equal("abcd", Assert.Single(read.Chunks).Text);
        Assert.Equal(4, read.NextCursor);
        Assert.Equal(4, read.RetainedUntilCursor);
        Assert.Equal(6, read.ObservedUntilCursor);
        Assert.True(read.RetentionLimitReached);
        Assert.False(read.Truncated);
    }

    [Fact]
    public void DisposeRemovesTemporarySpool()
    {
        var store = new ProcessOutputStore(1024);
        var path = store.SpoolPath;
        store.Append("output");

        Assert.True(File.Exists(path));
        store.Dispose();

        Assert.False(File.Exists(path));
    }
}
