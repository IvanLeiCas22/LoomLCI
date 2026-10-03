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
            "needle",
            work.Value.Id,
            contextLines: 1);
        Assert.True(searched.IsSuccess, searched.Error?.Message);
        var match = Assert.Single(searched.Value!.Matches);
        Assert.Equal("src/alpha.txt", match.Path);
        Assert.Equal(2, match.Line);
        Assert.Equal(1, match.Column);
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
        Assert.True(file.Truncated);
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
