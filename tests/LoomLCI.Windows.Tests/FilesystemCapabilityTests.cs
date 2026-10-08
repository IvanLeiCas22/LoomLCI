using System.Text;
using LoomLCI.Core;
using LoomLCI.Core.Filesystem;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Windows.Filesystem;

namespace LoomLCI.Windows.Tests;

public sealed class FilesystemCapabilityTests
{
    [Fact]
    public async Task DiscoverySearchAndReadUseWorkSessionBaseDirectory()
    {
        await using var fixture = new FilesystemFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "src", "alpha.txt"),
            "first line\nneedle value\nlast line");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "README.md"),
            "# Loom test");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var tree = await fixture.Filesystem.ListTreeAsync(".", work.Value!.Id, maxDepth: 3);
        Assert.True(tree.IsSuccess, tree.Error?.Message);
        Assert.Contains(tree.Value!.Entries, entry => entry.Path == "src/alpha.txt");

        var found = await fixture.Filesystem.FindPathsAsync(
            ".",
            [".txt"],
            FilesystemPathMatchMode.Suffix,
            FilesystemEntryType.File,
            work.Value.Id);
        Assert.True(found.IsSuccess, found.Error?.Message);
        Assert.Single(found.Value!.Matches);
        Assert.Equal("src/alpha.txt", found.Value.Matches[0].Path);

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            work.Value.Id,
            contextLines: 1);
        Assert.True(searched.IsSuccess, searched.Error?.Message);
        Assert.Equal(["needle"], searched.Value!.Queries);
        var match = Assert.Single(searched.Value.Matches);
        Assert.Equal("src/alpha.txt", match.Path);
        Assert.Equal(2, match.Line);
        var queryMatch = Assert.Single(match.QueryMatches);
        Assert.Equal("needle", queryMatch.Query);
        Assert.Equal(1, queryMatch.Column);
        Assert.Equal(["first line"], match.ContextBefore.Select(line => line.Text));
        Assert.Equal(["last line"], match.ContextAfter.Select(line => line.Text));
        Assert.All(match.ContextBefore, line => Assert.False(line.Truncated));
        Assert.All(match.ContextAfter, line => Assert.False(line.Truncated));

        var read = await fixture.Filesystem.ReadFilesAsync(
            [("src/alpha.txt", 2, 1)],
            work.Value.Id);
        Assert.True(read.IsSuccess, read.Error?.Message);
        var file = Assert.Single(read.Value!.Files);
        Assert.Equal(2, file.StartLine);
        Assert.Equal(2, file.EndLine);
        Assert.Equal("needle value", file.Text);
        Assert.True(file.HasMoreBefore);
        Assert.True(file.HasMoreAfter);
    }

    [Fact]
    public async Task ListTreeContinuationPreservesBfsAndAllowsPageSizeChanges()
    {
        await using var fixture = new FilesystemFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "a"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a", "inner.txt"), "inner");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "c.txt"), "c");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 2,
            maxEntries: 2);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(["a", "b.txt"], first.Value!.Entries.Select(entry => entry.Path));
        Assert.True(first.Value.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(first.Value.NextCursor));

        var second = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 2,
            maxEntries: 1,
            cursor: first.Value.NextCursor);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(["c.txt"], second.Value!.Entries.Select(entry => entry.Path));
        Assert.True(second.Value.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(second.Value.NextCursor));

        var third = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 2,
            maxEntries: 10,
            cursor: second.Value.NextCursor);
        Assert.True(third.IsSuccess, third.Error?.Message);
        Assert.Equal(["a/inner.txt"], third.Value!.Entries.Select(entry => entry.Path));
        Assert.False(third.Value.Truncated);
        Assert.Null(third.Value.NextCursor);
    }

    [Fact]
    public async Task FindPathsContinuationReturnsEveryMatchWithoutDuplicates()
    {
        await using var fixture = new FilesystemFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.cs"), "a");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "b.cs"), "b");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "other.txt"), "other");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sub", "c.cs"), "c");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.FindPathsAsync(
            ".",
            [".cs"],
            FilesystemPathMatchMode.Suffix,
            FilesystemEntryType.File,
            workId,
            maxResults: 1);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(["a.cs"], first.Value!.Matches.Select(match => match.Path));
        Assert.NotNull(first.Value.NextCursor);

        var second = await fixture.Filesystem.FindPathsAsync(
            ".",
            [".cs"],
            FilesystemPathMatchMode.Suffix,
            FilesystemEntryType.File,
            workId,
            maxResults: 2,
            cursor: first.Value.NextCursor);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(["b.cs", "sub/c.cs"], second.Value!.Matches.Select(match => match.Path));
        Assert.False(second.Value.Truncated);
        Assert.Null(second.Value.NextCursor);
    }

    [Fact]
    public async Task TraversalCursorRejectsMismatchedInputsAndMalformedValues()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "b.txt"), "b");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 2,
            maxEntries: 1);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.NotNull(first.Value!.NextCursor);

        var mismatched = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 3,
            maxEntries: 1,
            cursor: first.Value.NextCursor);
        Assert.False(mismatched.IsSuccess);
        Assert.Equal("invalid_argument", mismatched.Error?.Code);

        var malformed = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 2,
            maxEntries: 1,
            cursor: "not-a-valid-cursor");
        Assert.False(malformed.IsSuccess);
        Assert.Equal("invalid_argument", malformed.Error?.Code);
    }

    [Fact]
    public async Task TraversalCursorDetectsMutationAtResumePoint()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "c.txt"), "c");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 1,
            maxEntries: 1);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal("a.txt", Assert.Single(first.Value!.Entries).Path);
        Assert.NotNull(first.Value.NextCursor);

        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "aa.txt"), "aa");

        var resumed = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            maxDepth: 1,
            maxEntries: 1,
            cursor: first.Value.NextCursor);
        Assert.False(resumed.IsSuccess);
        Assert.Equal("conflict", resumed.Error?.Code);
        Assert.Equal("cursor_stale", resumed.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task ReadFilesReportsOmittedLinesBeforeAndAfterRange()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "ranges.txt"),
            "one\ntwo\nthree\nfour\nfive");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var read = await fixture.Filesystem.ReadFilesAsync(
            [
                ("ranges.txt", null, null),
                ("ranges.txt", 1, 2),
                ("ranges.txt", 3, 1),
                ("ranges.txt", 4, null)
            ],
            work.Value!.Id);

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Collection(
            read.Value!.Files,
            file =>
            {
                Assert.Equal((1, 5, 5), (file.StartLine, file.EndLine, file.TotalLines));
                Assert.False(file.HasMoreBefore);
                Assert.False(file.HasMoreAfter);
                Assert.Equal("one\ntwo\nthree\nfour\nfive", file.Text);
            },
            file =>
            {
                Assert.Equal((1, 2, 5), (file.StartLine, file.EndLine, file.TotalLines));
                Assert.False(file.HasMoreBefore);
                Assert.True(file.HasMoreAfter);
                Assert.Equal("one\ntwo", file.Text);
            },
            file =>
            {
                Assert.Equal((3, 3, 5), (file.StartLine, file.EndLine, file.TotalLines));
                Assert.True(file.HasMoreBefore);
                Assert.True(file.HasMoreAfter);
                Assert.Equal("three", file.Text);
            },
            file =>
            {
                Assert.Equal((4, 5, 5), (file.StartLine, file.EndLine, file.TotalLines));
                Assert.True(file.HasMoreBefore);
                Assert.False(file.HasMoreAfter);
                Assert.Equal("four\nfive", file.Text);
            });
    }

    [Fact]
    public async Task ReadFilesCanRangeReadTextFileLargerThanWholeFileLimit()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "large.txt");
        await using (var writer = new StreamWriter(path, append: false))
        {
            await writer.WriteAsync("first\ntarget\n");
            var block = string.Concat(Enumerable.Repeat(new string('x', 255) + "\n", 256));
            for (var i = 0; i < 260; i++)
            {
                await writer.WriteAsync(block);
            }
        }

        Assert.True(new FileInfo(path).Length > 16L * 1024 * 1024);

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var ranged = await fixture.Filesystem.ReadFilesAsync(
            [("large.txt", 2, 1)],
            work.Value!.Id);

        Assert.True(ranged.IsSuccess, ranged.Error?.Message);
        var file = Assert.Single(ranged.Value!.Files);
        Assert.Equal(2, file.StartLine);
        Assert.Equal(2, file.EndLine);
        Assert.Equal("target", file.Text);
        Assert.True(file.HasMoreBefore);
        Assert.True(file.HasMoreAfter);
        Assert.True(file.TotalLines > 60_000);

        var unbounded = await fixture.Filesystem.ReadFilesAsync(
            [("large.txt", null, null)],
            work.Value.Id);

        Assert.False(unbounded.IsSuccess);
        Assert.Equal("unsupported", unbounded.Error?.Code);
        Assert.Contains("line limit", unbounded.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TextSearchHandlesMultipleQueriesInOneTraversal()
    {
        await using var fixture = new FilesystemFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "src", "multi.txt"),
            "alpha beta\nbeta only\ngamma alpha");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["alpha", "beta", "missing"],
            workId,
            contextLines: 0);

        Assert.True(searched.IsSuccess, searched.Error?.Message);
        Assert.Equal(["alpha", "beta", "missing"], searched.Value!.Queries);
        Assert.Equal(1, searched.Value.FilesRead);
        Assert.False(searched.Value.Truncated);
        Assert.False(searched.Value.ResultLimitReached);
        Assert.False(searched.Value.ScanLimitReached);
        Assert.Equal(0, searched.Value.SkippedBinaryFileCount);
        Assert.Empty(searched.Value.SkippedBinaryFiles);
        Assert.Null(searched.Value.NextCursor);
        Assert.Collection(
            searched.Value.Matches,
            match =>
            {
                Assert.Equal(1, match.Line);
                Assert.Collection(
                    match.QueryMatches,
                    queryMatch =>
                    {
                        Assert.Equal("alpha", queryMatch.Query);
                        Assert.Equal(1, queryMatch.Column);
                    },
                    queryMatch =>
                    {
                        Assert.Equal("beta", queryMatch.Query);
                        Assert.Equal(7, queryMatch.Column);
                    });
            },
            match =>
            {
                Assert.Equal(2, match.Line);
                var queryMatch = Assert.Single(match.QueryMatches);
                Assert.Equal("beta", queryMatch.Query);
                Assert.Equal(1, queryMatch.Column);
            },
            match =>
            {
                Assert.Equal(3, match.Line);
                var queryMatch = Assert.Single(match.QueryMatches);
                Assert.Equal("alpha", queryMatch.Query);
                Assert.Equal(7, queryMatch.Column);
            });

        var limited = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["alpha", "beta"],
            workId,
            maxResults: 2,
            contextLines: 0);

        Assert.True(limited.IsSuccess, limited.Error?.Message);
        Assert.Equal(2, limited.Value!.Matches.Count);
        Assert.True(limited.Value.Truncated);
        Assert.True(limited.Value.ResultLimitReached);
        Assert.False(limited.Value.ScanLimitReached);
        Assert.Equal(0, limited.Value.SkippedBinaryFileCount);
        Assert.NotNull(limited.Value.NextCursor);

        var continued = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["alpha", "beta"],
            workId,
            maxResults: 10,
            contextLines: 0,
            cursor: limited.Value.NextCursor);
        Assert.True(continued.IsSuccess, continued.Error?.Message);
        Assert.Equal([3], continued.Value!.Matches.Select(match => match.Line));
        Assert.False(continued.Value.Truncated);
        Assert.Null(continued.Value.NextCursor);
    }

    [Fact]
    public async Task TextSearchFindsMatchBeyondFormerLargeFileLimit()
    {
        await using var fixture = new FilesystemFixture();
        var largePath = Path.Combine(fixture.Root, "large.txt");
        await using (var writer = new StreamWriter(
                         largePath,
                         append: false,
                         new UTF8Encoding(false)))
        {
            var block = new string('x', 1023) + "\n";
            for (var i = 0; i < 17 * 1024; i++)
            {
                await writer.WriteAsync(block);
            }
            await writer.WriteLineAsync("needle after old limit");
        }

        Assert.True(new FileInfo(largePath).Length > 16L * 1024 * 1024);
        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            work.Value!.Id,
            contextLines: 0);

        Assert.True(searched.IsSuccess, searched.Error?.Message);
        var match = Assert.Single(searched.Value!.Matches);
        Assert.Equal("large.txt", match.Path);
        Assert.Contains("needle after old limit", match.Text, StringComparison.Ordinal);
        Assert.False(searched.Value.Truncated);
        Assert.False(searched.Value.ScanLimitReached);
        Assert.Equal(0, searched.Value.SkippedBinaryFileCount);
    }

    [Fact]
    public async Task TextSearchReportsBinaryFilesFromPrefixHeuristic()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Root, "binary.dat"),
            Encoding.UTF8.GetBytes("alpha\0needle"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "text.txt"),
            "needle");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            work.Value!.Id,
            contextLines: 0);

        Assert.True(searched.IsSuccess, searched.Error?.Message);
        Assert.Single(searched.Value!.Matches);
        Assert.Equal(1, searched.Value.SkippedBinaryFileCount);
        Assert.Equal(["binary.dat"], searched.Value.SkippedBinaryFiles);
        Assert.False(searched.Value.Truncated);
    }

    [Fact]
    public async Task TextSearchScanBudgetContinuesWithoutGaps()
    {
        await using var fixture = new FilesystemFixture(maxSearchTotalBytes: 2048);
        var path = Path.Combine(fixture.Root, "paged.txt");
        await File.WriteAllTextAsync(
            path,
            new string('a', 1023) + "\n" +
            new string('b', 1023) + "\n" +
            new string('c', 400) + "\n" +
            "needle on fourth line");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            workId,
            contextLines: 0);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Empty(first.Value!.Matches);
        Assert.True(first.Value.Truncated);
        Assert.True(first.Value.ScanLimitReached);
        Assert.NotNull(first.Value.NextCursor);

        var second = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            workId,
            contextLines: 0,
            cursor: first.Value.NextCursor);
        Assert.True(second.IsSuccess, second.Error?.Message);
        var match = Assert.Single(second.Value!.Matches);
        Assert.Equal(4, match.Line);
        Assert.False(second.Value.Truncated);
        Assert.False(second.Value.ScanLimitReached);
        Assert.Null(second.Value.NextCursor);
    }

    [Fact]
    public async Task TextSearchCursorDetectsFileMutation()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "mutable.txt");
        await File.WriteAllTextAsync(path, "needle one\nneedle two");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            workId,
            maxResults: 1,
            contextLines: 0);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.NotNull(first.Value!.NextCursor);

        await File.AppendAllTextAsync(path, "\nchanged");

        var resumed = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            workId,
            maxResults: 10,
            contextLines: 0,
            cursor: first.Value.NextCursor);
        Assert.False(resumed.IsSuccess);
        Assert.Equal("conflict", resumed.Error?.Code);
        Assert.Equal("cursor_stale", resumed.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task TextSearchCursorRejectsDifferentSearchInputs()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "inputs.txt"),
            "needle one\nneedle two");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            workId,
            maxResults: 1,
            contextLines: 0);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.NotNull(first.Value!.NextCursor);

        var mismatched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["other"],
            workId,
            maxResults: 10,
            contextLines: 0,
            cursor: first.Value.NextCursor);
        Assert.False(mismatched.IsSuccess);
        Assert.Equal("invalid_argument", mismatched.Error?.Code);
    }

    [Fact]
    public async Task TextSearchContinuationRebuildsContextWithoutRepeatingMatches()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "context.txt"),
            "before\nhit one\nbetween\nhit two\nafter");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var first = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["hit"],
            workId,
            maxResults: 1,
            contextLines: 1);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var firstMatch = Assert.Single(first.Value!.Matches);
        Assert.Equal(2, firstMatch.Line);
        Assert.Equal(["before"], firstMatch.ContextBefore.Select(item => item.Text));
        Assert.Equal(["between"], firstMatch.ContextAfter.Select(item => item.Text));
        Assert.NotNull(first.Value.NextCursor);

        var second = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["hit"],
            workId,
            maxResults: 10,
            contextLines: 1,
            cursor: first.Value.NextCursor);
        Assert.True(second.IsSuccess, second.Error?.Message);
        var secondMatch = Assert.Single(second.Value!.Matches);
        Assert.Equal(4, secondMatch.Line);
        Assert.Equal(["between"], secondMatch.ContextBefore.Select(item => item.Text));
        Assert.Equal(["after"], secondMatch.ContextAfter.Select(item => item.Text));
        Assert.Null(second.Value.NextCursor);
    }

    [Fact]
    public async Task TextSearchBoundsMatchAndContextExcerpts()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "wide.txt"),
            new string('a', 800) + "\n" +
            new string('x', 900) + " needle " + new string('y', 900) + "\n" +
            new string('z', 800));

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["needle"],
            work.Value!.Id,
            contextLines: 1);
        Assert.True(searched.IsSuccess, searched.Error?.Message);

        var match = Assert.Single(searched.Value!.Matches);
        Assert.True(match.TextTruncated);
        Assert.True(match.Text.Length <= StreamingTextSearchReader.MaxExcerptChars);
        Assert.Contains("needle", match.Text, StringComparison.Ordinal);
        Assert.True(match.TextStartColumn > 1);
        Assert.All(
            match.ContextBefore.Concat(match.ContextAfter),
            excerpt =>
            {
                Assert.True(excerpt.Truncated);
                Assert.True(excerpt.Text.Length <= StreamingTextSearchReader.MaxExcerptChars);
            });
    }

    [Fact]
    public async Task TextSearchGroupsOverlappingQueriesByLine()
    {
        await using var fixture = new FilesystemFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "overlap.txt"),
            "call SearchTextAsync now");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["SearchText", "SearchTextAsync"],
            work.Value!.Id,
            caseSensitive: true,
            contextLines: 0);

        Assert.True(searched.IsSuccess, searched.Error?.Message);
        var match = Assert.Single(searched.Value!.Matches);
        Assert.Equal(1, match.Line);
        Assert.Collection(
            match.QueryMatches,
            queryMatch =>
            {
                Assert.Equal("SearchText", queryMatch.Query);
                Assert.Equal(6, queryMatch.Column);
            },
            queryMatch =>
            {
                Assert.Equal("SearchTextAsync", queryMatch.Query);
                Assert.Equal(6, queryMatch.Column);
            });
    }

    [Fact]
    public async Task RecursiveDiscoveryPrunesGeneratedDirectoriesByDefault()
    {
        await using var fixture = new FilesystemFixture();
        var generatedDirectories = new[]
        {
            ".git",
            ".vs",
            ".venv",
            "__pycache__",
            "bin",
            "node_modules",
            "obj"
        };

        foreach (var directory in generatedDirectories)
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, directory));
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Root, directory, "hidden.txt"),
                "generated needle");
        }

        Directory.CreateDirectory(Path.Combine(fixture.Root, ".obsidian"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, ".obsidian", "visible.txt"),
            "visible content");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var tree = await fixture.Filesystem.ListTreeAsync(".", workId, maxDepth: 3);
        Assert.True(tree.IsSuccess, tree.Error?.Message);
        foreach (var directory in generatedDirectories)
        {
            var entry = Assert.Single(tree.Value!.Entries, item => item.Path == directory);
            Assert.Equal("generated", entry.ChildrenExcluded);
            Assert.DoesNotContain(tree.Value.Entries, item => item.Path == $"{directory}/hidden.txt");
        }

        Assert.Contains(tree.Value!.Entries, item => item.Path == ".obsidian/visible.txt");

        var found = await fixture.Filesystem.FindPathsAsync(
            ".",
            ["hidden.txt"],
            FilesystemPathMatchMode.Suffix,
            FilesystemEntryType.File,
            workId);
        Assert.True(found.IsSuccess, found.Error?.Message);
        Assert.Empty(found.Value!.Matches);

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["generated needle"],
            workId);
        Assert.True(searched.IsSuccess, searched.Error?.Message);
        Assert.Empty(searched.Value!.Matches);
    }

    [Fact]
    public async Task ObsidianPluginsArePrunedWithoutHidingTheVault()
    {
        await using var fixture = new FilesystemFixture();
        var obsidian = Path.Combine(fixture.Root, ".obsidian");
        var plugins = Path.Combine(obsidian, "plugins");
        var plugin = Path.Combine(plugins, "example-plugin");
        Directory.CreateDirectory(plugin);
        await File.WriteAllTextAsync(Path.Combine(obsidian, "workspace.json"), "vault visible");
        await File.WriteAllTextAsync(Path.Combine(plugin, "main.js"), "plugin needle");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var tree = await fixture.Filesystem.ListTreeAsync(".", workId, maxDepth: 4);
        Assert.True(tree.IsSuccess, tree.Error?.Message);
        Assert.Contains(tree.Value!.Entries, entry => entry.Path == ".obsidian/workspace.json");
        Assert.Equal(
            "generated",
            Assert.Single(tree.Value.Entries, entry => entry.Path == ".obsidian/plugins").ChildrenExcluded);
        Assert.DoesNotContain(tree.Value.Entries, entry => entry.Path == ".obsidian/plugins/example-plugin/main.js");

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["plugin needle"],
            workId);
        Assert.True(searched.IsSuccess, searched.Error?.Message);
        Assert.Empty(searched.Value!.Matches);

        var included = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["plugin needle"],
            workId,
            includeGenerated: true);
        Assert.True(included.IsSuccess, included.Error?.Message);
        Assert.Equal(
            ".obsidian/plugins/example-plugin/main.js",
            Assert.Single(included.Value!.Matches).Path);

        var direct = await fixture.Filesystem.SearchTextAsync(
            ".obsidian/plugins",
            ["plugin needle"],
            workId);
        Assert.True(direct.IsSuccess, direct.Error?.Message);
        Assert.Equal("example-plugin/main.js", Assert.Single(direct.Value!.Matches).Path);
    }

    [Fact]
    public async Task GeneratedDirectoriesCanBeIncludedOrTargetedDirectly()
    {
        await using var fixture = new FilesystemFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "bin"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "bin", "generated.txt"),
            "generated needle");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var included = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            includeGenerated: true,
            maxDepth: 3);
        Assert.True(included.IsSuccess, included.Error?.Message);
        Assert.Contains(included.Value!.Entries, entry => entry.Path == "bin/generated.txt");
        Assert.Null(Assert.Single(included.Value.Entries, entry => entry.Path == "bin").ChildrenExcluded);

        var directTree = await fixture.Filesystem.ListTreeAsync("bin", workId, maxDepth: 2);
        Assert.True(directTree.IsSuccess, directTree.Error?.Message);
        Assert.Contains(directTree.Value!.Entries, entry => entry.Path == "generated.txt");

        var directSearch = await fixture.Filesystem.SearchTextAsync(
            "bin",
            ["generated needle"],
            workId);
        Assert.True(directSearch.IsSuccess, directSearch.Error?.Message);
        Assert.Equal("generated.txt", Assert.Single(directSearch.Value!.Matches).Path);
    }

    [Fact]
    public async Task CustomDirectoryExclusionsAreAdditive()
    {
        await using var fixture = new FilesystemFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".obsidian"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".obsidian", "note.md"), "secret needle");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "bin", "generated.txt"), "generated needle");

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var tree = await fixture.Filesystem.ListTreeAsync(
            ".",
            workId,
            includeGenerated: true,
            excludeDirectories: [".OBSIDIAN"],
            maxDepth: 3);
        Assert.True(tree.IsSuccess, tree.Error?.Message);
        Assert.Equal(
            "excluded",
            Assert.Single(tree.Value!.Entries, entry => entry.Path == ".obsidian").ChildrenExcluded);
        Assert.DoesNotContain(tree.Value.Entries, entry => entry.Path == ".obsidian/note.md");
        Assert.Contains(tree.Value.Entries, entry => entry.Path == "bin/generated.txt");

        var searched = await fixture.Filesystem.SearchTextAsync(
            ".",
            ["secret needle"],
            workId,
            includeGenerated: true,
            excludeDirectories: [".OBSIDIAN"]);
        Assert.True(searched.IsSuccess, searched.Error?.Message);
        Assert.Empty(searched.Value!.Matches);
    }

    [Fact]
    public async Task TraversalRejectsDirectoryPathsAsExclusions()
    {
        await using var fixture = new FilesystemFixture();
        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var result = await fixture.Filesystem.ListTreeAsync(
            ".",
            work.Value!.Id,
            excludeDirectories: ["nested/path"]);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
    }

    [Fact]
    public async Task AbsolutePathCanBeReadWithoutWorkSession()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "absolute.txt");
        await File.WriteAllTextAsync(path, "absolute-ok");

        var read = await fixture.Filesystem.ReadFilesAsync([(path, null, null)]);

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal("absolute-ok", Assert.Single(read.Value!.Files).Text);
    }

    [Fact]
    public async Task RelativePathWithoutWorkSessionIsRejected()
    {
        await using var fixture = new FilesystemFixture();

        var result = await fixture.Filesystem.ListTreeAsync(".");

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
    }

    [Fact]
    public async Task StructuredPatchAndDirectoryOperationsRoundTrip()
    {
        await using var fixture = new FilesystemFixture();
        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);
        var workId = work.Value!.Id;

        var create = await fixture.Filesystem.CreateDirectoryAsync("notes", workId);
        Assert.True(create.IsSuccess, create.Error?.Message);

        var write = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Write,
                Path: "notes/a.txt",
                Content: "alpha beta")],
            workId);
        Assert.True(write.IsSuccess, write.Error?.Message);

        var replace = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace,
                Path: "notes/a.txt",
                OldText: "beta",
                NewText: "gamma")],
            workId);
        Assert.True(replace.IsSuccess, replace.Error?.Message);
        Assert.Equal("alpha gamma", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "notes", "a.txt")));

        var move = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Move,
                FromPath: "notes/a.txt",
                ToPath: "notes/b.txt")],
            workId);
        Assert.True(move.IsSuccess, move.Error?.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "notes", "b.txt")));

        var delete = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Delete,
                Path: "notes/b.txt")],
            workId);
        Assert.True(delete.IsSuccess, delete.Error?.Message);

        var removeDirectory = await fixture.Filesystem.DeleteDirectoryAsync("notes", workId);
        Assert.True(removeDirectory.IsSuccess, removeDirectory.Error?.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "notes")));
    }

    [Fact]
    public async Task DeleteAndMoveSupportLargeBinaryFilesWithoutTextValidation()
    {
        await using var fixture = new FilesystemFixture();
        var source = Path.Combine(fixture.Root, "large.bin");
        var destination = Path.Combine(fixture.Root, "destination.bin");

        await using (var stream = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(17L * 1024 * 1024);
        }
        await File.WriteAllBytesAsync(destination, [1, 2, 3, 4]);

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var move = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Move,
                FromPath: "large.bin",
                ToPath: "destination.bin",
                Overwrite: true)],
            work.Value!.Id);

        Assert.True(move.IsSuccess, move.Error?.Message);
        Assert.False(File.Exists(source));
        Assert.Equal(17L * 1024 * 1024, new FileInfo(destination).Length);

        var delete = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Delete,
                Path: "destination.bin")],
            work.Value.Id);

        Assert.True(delete.IsSuccess, delete.Error?.Message);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*.bak"));
    }

    [Fact]
    public async Task LargeFileDeleteRollsBackFromDiskBackupWhenLaterChangeFails()
    {
        await using var fixture = new FilesystemFixture();
        var large = Path.Combine(fixture.Root, "large.bin");
        await using (var stream = new FileStream(large, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(17L * 1024 * 1024);
        }
        Directory.CreateDirectory(Path.Combine(fixture.Root, "blocked"));

        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [
                new FilesystemPatchChange(
                    FilesystemPatchOperation.Delete,
                    Path: "large.bin"),
                new FilesystemPatchChange(
                    FilesystemPatchOperation.Write,
                    Path: "blocked",
                    Content: "cannot write over a directory",
                    Overwrite: true)
            ],
            work.Value!.Id);

        Assert.False(result.IsSuccess);
        Assert.True(File.Exists(large));
        Assert.Equal(17L * 1024 * 1024, new FileInfo(large).Length);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*.bak"));
    }

    [Fact]
    public async Task ReplaceRejectsUnexpectedOccurrenceCountWithoutMutation()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "count.txt");
        await File.WriteAllTextAsync(path, "x x");
        var work = fixture.Sessions.Create(fixture.Root);
        Assert.True(work.IsSuccess);

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace,
                Path: "count.txt",
                OldText: "x",
                NewText: "y",
                ExpectedOccurrences: 1)],
            work.Value!.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("conflict", result.Error?.Code);
        Assert.Equal("x x", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReplaceRejectsInvalidUtf8WithoutChangingBytes()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "invalid.txt");
        byte[] original = [0xFF, (byte)'a', (byte)'X', (byte)'b'];
        await File.WriteAllBytesAsync(path, original);

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace,
                Path: path,
                OldText: "a",
                NewText: "A")]);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task ReplacePreservesEncodingAndBom(string format)
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, format + ".txt");
        Encoding encoding = format switch
        {
            "utf8" => new UTF8Encoding(false, true),
            "utf8bom" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            "utf32be" => new UTF32Encoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("alpha beta\r\n")];
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("alpha gamma\r\n")];
        await File.WriteAllBytesAsync(path, original);

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace, Path: path,
                OldText: "beta", NewText: "gamma")]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task ReplaceRejectsInvalidUtf16SurrogateWithoutChangingBytes()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "broken-utf16.txt");
        byte[] original = [0xFF, 0xFE, 0x00, 0xD8, 0x61, 0x00];
        await File.WriteAllBytesAsync(path, original);

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace, Path: path,
                OldText: "a", NewText: "b")]);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReplaceRejectsOversizedFinalContentWithoutMutation()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "expand.txt");
        await File.WriteAllTextAsync(path, "q q q");

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace, Path: path,
                OldText: "q", NewText: new string('X', 5_600_000),
                ExpectedOccurrences: 3)]);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("q q q", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task OverwriteEnforcesEncodedSizeAndPreservesUtf32OnSuccess()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "wide.txt");
        var encoding = new UTF32Encoding(false, true, true);
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("old")];
        await File.WriteAllBytesAsync(path, original);

        var denied = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Write, Path: path,
                Content: new string('x', 4_300_000), Overwrite: true)]);
        Assert.False(denied.IsSuccess);
        Assert.Equal("unsupported", denied.Error?.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

        var allowed = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Write, Path: path,
                Content: "new", Overwrite: true)]);
        Assert.True(allowed.IsSuccess, allowed.Error?.Message);
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("new")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task OverwriteRejectsInvalidExistingEncoding()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "invalid-overwrite.txt");
        byte[] original = [0xFE, (byte)'a'];
        await File.WriteAllBytesAsync(path, original);

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Write, Path: path,
                Content: "safe text", Overwrite: true)]);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailureBeforePublishLeavesExistingFileUnchangedAndNoTemporaryFiles()
    {
        await using var fixture = new FilesystemFixture(
            patchStageHook: (stage, _) =>
            {
                if (stage == "before_publish")
                {
                    throw new IOException("Injected staging failure");
                }
            });
        var path = Path.Combine(fixture.Root, "original.txt");
        await File.WriteAllTextAsync(path, "one two");
        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace, Path: path,
                OldText: "one", NewText: "ONE")]);

        Assert.False(result.IsSuccess);
        Assert.Equal("one two", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task OptimisticConflictPreservesExternalChanges()
    {
        var changed = false;
        await using var fixture = new FilesystemFixture(
            patchStageHook: (stage, path) =>
            {
                if (stage == "before_publish" && !changed)
                {
                    File.WriteAllText(path, "external edit");
                    changed = true;
                }
            });
        var path = Path.Combine(fixture.Root, "conflict.txt");
        await File.WriteAllTextAsync(path, "original");

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace, Path: path,
                OldText: "original", NewText: "updated")]);

        Assert.False(result.IsSuccess);
        Assert.Equal("conflict", result.Error?.Code);
        Assert.Equal("external edit", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task FailureAfterPublishingSecondFileRestoresWholeBatch()
    {
        var changesPublished = 0;
        await using var fixture = new FilesystemFixture(
            patchStageHook: (stage, _) =>
            {
                if (stage == "after_change" && ++changesPublished == 2)
                {
                    throw new IOException("Injected failure after second publication");
                }
            });
        var first = Path.Combine(fixture.Root, "one.txt");
        var second = Path.Combine(fixture.Root, "two.txt");
        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "two");

        var result = await fixture.Filesystem.ApplyPatchAsync(
        [
            new FilesystemPatchChange(
                FilesystemPatchOperation.Replace, Path: first,
                OldText: "one", NewText: "ONE"),
            new FilesystemPatchChange(
                FilesystemPatchOperation.Write, Path: second,
                Content: "TWO", Overwrite: true)
        ]);

        Assert.False(result.IsSuccess);
        Assert.Equal("one", await File.ReadAllTextAsync(first));
        Assert.Equal("two", await File.ReadAllTextAsync(second));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task FailedRollbackReportsErrorAndKeepsRecoverableBackup()
    {
        FileStream? lockedFile = null;
        await using var fixture = new FilesystemFixture(
            patchStageHook: (stage, path) =>
            {
                if (stage == "after_change")
                {
                    lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    throw new IOException("Injected failure with locked destination");
                }
            });
        var path = Path.Combine(fixture.Root, "recover.txt");
        await File.WriteAllTextAsync(path, "original");
        try
        {
            var result = await fixture.Filesystem.ApplyPatchAsync(
                [new FilesystemPatchChange(
                    FilesystemPatchOperation.Replace, Path: path,
                    OldText: "original", NewText: "updated")]);

            Assert.False(result.IsSuccess);
            Assert.Equal("rollback_failed", result.Error?.Code);
            Assert.NotNull(result.Error?.Details);
        }
        finally
        {
            lockedFile?.Dispose();
        }

        var backup = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*.bak"));
        Assert.Equal("original", await File.ReadAllTextAsync(backup));
        File.Replace(backup, path, null, ignoreMetadataErrors: true);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task CancellationAfterStagingLeavesOriginalAndNoTemporaryFiles()
    {
        using var source = new CancellationTokenSource();
        await using var fixture = new FilesystemFixture(
            patchStageHook: (stage, _) =>
            {
                if (stage == "before_publish")
                {
                    source.Cancel();
                }
            });
        var path = Path.Combine(fixture.Root, "cancelled.txt");
        await File.WriteAllTextAsync(path, "unchanged");

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Replace, Path: path,
                OldText: "unchanged", NewText: "changed")],
            cancellationToken: source.Token);
        Assert.False(result.IsSuccess);
        Assert.Equal("cancelled", result.Error?.Code);

        Assert.Equal("unchanged", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task BatchFailureRestoresNewFilesAndBinaryMoveOverwrites()
    {
        await using var fixture = new FilesystemFixture(
            patchStageHook: (stage, _) =>
            {
                if (stage == "after_change")
                {
                    throw new IOException("Injected failure after binary move");
                }
            });
        var source = Path.Combine(fixture.Root, "src.dat");
        var destination = Path.Combine(fixture.Root, "dst.dat");
        byte[] fromBytes = [1, 2, 3];
        byte[] toBytes = [4, 5, 6];
        await File.WriteAllBytesAsync(source, fromBytes);
        await File.WriteAllBytesAsync(destination, toBytes);

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Move, FromPath: source,
                ToPath: destination, Overwrite: true)]);

        Assert.False(result.IsSuccess);
        Assert.Equal(fromBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(toBytes, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task ConcurrentPatchBatchesCanOverwriteSameFileWithoutInterleaving()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "shared.txt");
        await File.WriteAllTextAsync(path, "original");
        var requests = Enumerable.Range(0, 8).Select(index =>
            fixture.Filesystem.ApplyPatchAsync(
                [new FilesystemPatchChange(
                    FilesystemPatchOperation.Write, Path: path,
                    Content: "value" + index, Overwrite: true)]));
        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        Assert.StartsWith("value", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task WriteRejectsInvalidInputSurrogateWithoutMutation()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "surrogate.txt");

        var result = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Write, Path: path,
                Content: "broken " + '\uD800')]);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task Utf8BomOverwriteHonorsExactSixteenMiBBoundary()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "exact-boundary.txt");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, (byte)'a']);

        var exact = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Write, Path: path,
                Content: new string('A', 16 * 1024 * 1024 - 3),
                Overwrite: true)]);
        Assert.True(exact.IsSuccess, exact.Error?.Message);
        Assert.Equal(16L * 1024 * 1024, new FileInfo(path).Length);

        var denied = await fixture.Filesystem.ApplyPatchAsync(
            [new FilesystemPatchChange(
                FilesystemPatchOperation.Write, Path: path,
                Content: new string('B', 16 * 1024 * 1024 - 2),
                Overwrite: true)]);
        Assert.False(denied.IsSuccess);
        Assert.Equal("unsupported", denied.Error?.Code);
        Assert.Equal(16L * 1024 * 1024, new FileInfo(path).Length);
        var prefix = new byte[4];
        using (var stream = File.OpenRead(path))
        {
            Assert.Equal(4, stream.Read(prefix));
        }
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A' }, prefix);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    [Fact]
    public async Task ReplacementPreservesDestinationHiddenAttribute()
    {
        await using var fixture = new FilesystemFixture();
        var path = Path.Combine(fixture.Root, "hidden.txt");
        await File.WriteAllTextAsync(path, "old");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

        try
        {
            var result = await fixture.Filesystem.ApplyPatchAsync(
                [new FilesystemPatchChange(
                    FilesystemPatchOperation.Replace, Path: path,
                    OldText: "old", NewText: "new")]);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
            Assert.Equal("new", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.loomlci-*"));
    }

    private sealed class FilesystemFixture : IAsyncDisposable
    {
        public FilesystemFixture(
            long? maxSearchTotalBytes = null,
            Action<string, string>? patchStageHook = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "LoomLCI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(Resources, Events);
            Invocations = new InvocationRunner(Events, Sessions);
            Filesystem = new FilesystemCapability(
                maxSearchTotalBytes.HasValue || patchStageHook is not null
                    ? new WindowsFilesystemProvider(
                        maxSearchTotalBytes ?? 64L * 1024 * 1024,
                        patchStageHook)
                    : new WindowsFilesystemProvider(),
                Invocations,
                Events);
        }

        public string Root { get; }
        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public FilesystemCapability Filesystem { get; }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
