using System.Text.Json;
using LoomLCI.Core.Filesystem;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.VisualFiles;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Tests;

public sealed class PythonFilesystemBridgeModuleTests
{
    [Fact]
    public void ExposesExpectedMethods()
    {
        using var fixture = new BridgeFixture();

        Assert.Equal(
            [
                "fs.apply_patch",
                "fs.find_paths",
                "fs.list_tree",
                "fs.manage_directory",
                "fs.read_files",
                "fs.read_pdf",
                "fs.search_text"
            ],
            fixture.Module.Methods);
    }

    [Fact]
    public async Task ListTreeUsesWorkBaseDirectoryAndMapsSnakeCase()
    {
        using var fixture = new BridgeFixture();

        var result = await fixture.Module.DispatchAsync(
            fixture.WorkId,
            Call(
                "fs.list_tree",
                new
                {
                    path = ".",
                    max_depth = 2,
                    max_entries = 10
                }),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            Path.GetFullPath(fixture.BaseDirectory),
            fixture.FilesystemProvider.LastListRoot);

        var root = result.Value;
        Assert.Equal(
            2,
            root.GetProperty("max_depth").GetInt32());
        Assert.Equal(
            10,
            root.GetProperty("max_entries").GetInt32());

        var entry = Assert.Single(
            root.GetProperty("entries").EnumerateArray());
        Assert.Equal(
            "file",
            entry.GetProperty("type").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            entry.GetProperty("children_excluded").ValueKind);
        Assert.False(
            root.TryGetProperty("maxDepth", out _));
    }

    [Fact]
    public async Task UnknownArgumentFieldIsRejected()
    {
        using var fixture = new BridgeFixture();

        var result = await fixture.Module.DispatchAsync(
            fixture.WorkId,
            Call(
                "fs.list_tree",
                new
                {
                    path = ".",
                    typo = true
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "invalid_argument",
            result.Error?.Code);
    }

    [Fact]
    public async Task FilesystemErrorPassesThroughUnchanged()
    {
        using var fixture = new BridgeFixture();
        fixture.FilesystemProvider.ReadFilesResult =
            LoomResult<FilesystemReadFilesResult>.Failure(
                LoomErrors.NotFound(
                    "missing"));

        var result = await fixture.Module.DispatchAsync(
            fixture.WorkId,
            Call(
                "fs.read_files",
                new
                {
                    files = new[]
                    {
                        new
                        {
                            path = "missing.txt",
                            offset = (int?)null,
                            limit = (int?)null
                        }
                    }
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "not_found",
            result.Error?.Code);
        Assert.Equal(
            "missing",
            result.Error?.Message);
    }

    [Fact]
    public async Task OversizedStructuredResultIsRecoverable()
    {
        using var fixture = new BridgeFixture();
        var huge = new string(
            'x',
            PythonBridgeLimits.MaxResultFrameBytes + 1024);

        fixture.FilesystemProvider.ReadFilesResult =
            LoomResult<FilesystemReadFilesResult>.Success(
                new FilesystemReadFilesResult(
                [
                    new FilesystemReadFileResult(
                        "huge.txt",
                        Path.Combine(
                            fixture.BaseDirectory,
                            "huge.txt"),
                        1,
                        1,
                        1,
                        false,
                        false,
                        huge)
                ]));

        var result = await fixture.Module.DispatchAsync(
            fixture.WorkId,
            Call(
                "fs.read_files",
                new
                {
                    files = new[]
                    {
                        new
                        {
                            path = "huge.txt",
                            offset = (int?)null,
                            limit = (int?)1
                        }
                    }
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "unsupported",
            result.Error?.Code);
        Assert.Equal(
            "bridge_payload_too_large",
            result.Error?.Details?["reason"]);
        Assert.Equal(
            PythonBridgeLimits.MaxResultFrameBytes,
            result.Error?.Details?["max_bridge_result_bytes"]);
    }

    [Fact]
    public async Task ReadPdfMapsTextMetadataToSnakeCase()
    {
        using var fixture = new BridgeFixture();

        var result = await fixture.Module.DispatchAsync(
            fixture.WorkId,
            Call(
                "fs.read_pdf",
                new
                {
                    path = "sample.pdf",
                    start_page = 1,
                    max_pages = 1
                }),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var root = result.Value;
        Assert.Equal(
            1,
            root.GetProperty("page_count").GetInt32());
        Assert.Equal(
            "hello",
            Assert.Single(
                    root.GetProperty("pages")
                        .EnumerateArray())
                .GetProperty("text")
                .GetString());
    }

    private static PythonBridgeCall Call(
        string method,
        object arguments)
        => new(
            method,
            JsonSerializer.SerializeToElement(
                arguments));

    private sealed class BridgeFixture : IDisposable
    {
        private readonly WorkSessionManager _sessions;

        public BridgeFixture()
        {
            BaseDirectory = Path.Combine(
                Path.GetTempPath(),
                "loom-python-fs-bridge-core",
                Guid.NewGuid().ToString("N"));

            var events = new LoomEventBus();
            var resources = new ResourceRegistry();
            _sessions = new WorkSessionManager(
                resources,
                events);
            var invocations = new InvocationRunner(
                events,
                _sessions);

            FilesystemProvider =
                new FakeFilesystemProvider();
            var filesystem = new FilesystemCapability(
                FilesystemProvider,
                invocations,
                events);

            var visualFiles = new VisualFilesCapability(
                new FakeVisualFilesProvider(
                    BaseDirectory),
                invocations);

            Module = new PythonFilesystemBridgeModule(
                filesystem,
                visualFiles);

            var work = _sessions.Create(
                BaseDirectory);
            Assert.True(
                work.IsSuccess,
                work.Error?.Message);
            WorkId = work.Value!.Id;
        }

        public string BaseDirectory { get; }
        public WorkId WorkId { get; }
        public FakeFilesystemProvider FilesystemProvider { get; }
        public PythonFilesystemBridgeModule Module { get; }

        public void Dispose()
            => _sessions.DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
    }

    private sealed class FakeFilesystemProvider
        : IFilesystemProvider
    {
        public string? LastListRoot { get; private set; }

        public LoomResult<FilesystemReadFilesResult> ReadFilesResult { get; set; } =
            LoomResult<FilesystemReadFilesResult>.Failure(
                LoomErrors.NotFound(
                    "not configured"));

        public Task<LoomResult<FilesystemListTreeResult>> ListTreeAsync(
            string root,
            FilesystemTraversalOptions traversal,
            int maxDepth,
            int maxEntries,
            string? cursor,
            CancellationToken cancellationToken)
        {
            LastListRoot = root;
            return Task.FromResult(
                LoomResult<FilesystemListTreeResult>.Success(
                    new FilesystemListTreeResult(
                        root,
                        maxDepth,
                        maxEntries,
                        [
                            new FilesystemEntry(
                                "a.txt",
                                "a.txt",
                                FilesystemEntryType.File,
                                5,
                                1,
                                null)
                        ],
                        false,
                        null)));
        }

        public Task<LoomResult<FilesystemReadFilesResult>> ReadFilesAsync(
            IReadOnlyList<FilesystemReadFileRequest> files,
            CancellationToken cancellationToken)
            => Task.FromResult(
                ReadFilesResult);

        public Task<LoomResult<FilesystemFindPathsResult>> FindPathsAsync(
            string root,
            IReadOnlyList<string> queries,
            FilesystemPathMatchMode matchMode,
            FilesystemEntryType? type,
            FilesystemTraversalOptions traversal,
            int maxDepth,
            int maxResults,
            string? cursor,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LoomResult<FilesystemSearchTextResult>> SearchTextAsync(
            string root,
            IReadOnlyList<string> queries,
            bool caseSensitive,
            FilesystemTraversalOptions traversal,
            int maxDepth,
            int maxResults,
            int contextLines,
            string? cursor,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LoomResult<FilesystemPatchResult>> ApplyPatchAsync(
            IReadOnlyList<FilesystemPatchChange> changes,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LoomResult<FilesystemDirectoryResult>> CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LoomResult<FilesystemDirectoryResult>> DeleteDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeVisualFilesProvider(
        string baseDirectory)
        : IVisualFilesProvider
    {
        public Task<LoomResult<PdfTextReadResult>> ReadPdfTextAsync(
            PdfTextReadRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                LoomResult<PdfTextReadResult>.Success(
                    new PdfTextReadResult(
                        request.RequestedPath,
                        Path.Combine(
                            baseDirectory,
                            request.RequestedPath),
                        128,
                        1,
                        1,
                        1,
                        5,
                        false,
                        false,
                        null,
                        [
                            new PdfTextPageResult(
                                1,
                                "hello",
                                5,
                                false)
                        ])));

        public Task<LoomResult<VisualImageResult>> ReadImageAsync(
            VisualImageRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LoomResult<PdfPageRenderResult>> RenderPdfPageAsync(
            PdfPageRenderRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
