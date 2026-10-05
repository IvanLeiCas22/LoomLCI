namespace LoomLCI.Core.VisualFiles;

public static class VisualFilesLimits
{
    public const int MaxImageBytes = 6 * 1024 * 1024;
}

public sealed record VisualImageRequest(
    string RequestedPath,
    string FullPath);

public sealed record VisualImageResult(
    string RequestedPath,
    string FullPath,
    string MimeType,
    byte[] Bytes);

public interface IVisualFilesProvider
{
    Task<LoomResult<VisualImageResult>> ReadImageAsync(
        VisualImageRequest request,
        CancellationToken cancellationToken);
}
