using LoomLCI.Core.Filesystem;
using LoomLCI.Core.Invocations;

namespace LoomLCI.Core.VisualFiles;

public sealed class VisualFilesCapability
{
    private readonly IVisualFilesProvider _provider;
    private readonly InvocationRunner _invocations;

    public VisualFilesCapability(
        IVisualFilesProvider provider,
        InvocationRunner invocations)
    {
        _provider = provider;
        _invocations = invocations;
    }

    public Task<LoomResult<VisualImageResult>> ViewImageAsync(
        string path,
        WorkId? workId = null,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "filesystem.view_image",
            workId,
            async (context, token) =>
            {
                var resolved = FilesystemPathResolver.Resolve(
                    path,
                    context.WorkSession?.BaseDirectory);
                if (!resolved.IsSuccess)
                {
                    return LoomResult<VisualImageResult>.Failure(resolved.Error!);
                }

                return await _provider.ReadImageAsync(
                    new VisualImageRequest(path, resolved.Value!),
                    token).ConfigureAwait(false);
            },
            cancellationToken);

    public Task<LoomResult<PdfTextReadResult>> ReadPdfTextAsync(
        string path,
        WorkId? workId = null,
        int startPage = 1,
        int maxPages = 10,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "filesystem.read_pdf",
            workId,
            async (context, token) =>
            {
                if (startPage < 1)
                {
                    return LoomResult<PdfTextReadResult>.Failure(
                        LoomErrors.InvalidArgument("startPage must be at least 1."));
                }

                if (maxPages is < 1 or > VisualFilesLimits.MaxPdfPagesPerRead)
                {
                    return LoomResult<PdfTextReadResult>.Failure(
                        LoomErrors.InvalidArgument(
                            $"maxPages must be between 1 and {VisualFilesLimits.MaxPdfPagesPerRead}."));
                }

                var resolved = FilesystemPathResolver.Resolve(
                    path,
                    context.WorkSession?.BaseDirectory);
                if (!resolved.IsSuccess)
                {
                    return LoomResult<PdfTextReadResult>.Failure(resolved.Error!);
                }

                return await _provider.ReadPdfTextAsync(
                    new PdfTextReadRequest(
                        path,
                        resolved.Value!,
                        startPage,
                        maxPages),
                    token).ConfigureAwait(false);
            },
            cancellationToken);
}
