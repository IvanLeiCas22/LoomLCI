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
}
