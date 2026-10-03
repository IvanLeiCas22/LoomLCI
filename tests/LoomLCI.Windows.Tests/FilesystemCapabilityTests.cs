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
        Assert.Equal(["first line"], match.ContextBefore);
        Assert.Equal(["last line"], match.ContextAfter);

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

    private sealed class FilesystemFixture : IAsyncDisposable
    {
        public FilesystemFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "LoomLCI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(Resources, Events);
            Invocations = new InvocationRunner(Events, Sessions);
            Filesystem = new FilesystemCapability(
                new WindowsFilesystemProvider(),
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
