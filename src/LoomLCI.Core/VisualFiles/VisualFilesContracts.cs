namespace LoomLCI.Core.VisualFiles;

public static class VisualFilesLimits
{
    public const int MaxImageBytes = 6 * 1024 * 1024;
    public const long MaxPdfBytes = 64L * 1024 * 1024;
    public const int MaxPdfPagesPerRead = 25;
    public const int MaxPageTextCodePoints = 65_536;
    public const int MaxTotalTextCodePoints = 262_144;
}

public sealed record VisualImageRequest(
    string RequestedPath,
    string FullPath);

public sealed record VisualImageResult(
    string RequestedPath,
    string FullPath,
    string MimeType,
    byte[] Bytes);

public sealed record PdfTextReadRequest(
    string RequestedPath,
    string FullPath,
    int StartPage,
    int MaxPages);

public sealed record PdfTextPageResult(
    int PageNumber,
    string Text,
    int TextLength,
    bool TextTruncated);

public sealed record PdfTextReadResult(
    string RequestedPath,
    string FullPath,
    long SizeBytes,
    int PageCount,
    int StartPage,
    int EndPage,
    int TotalTextLength,
    bool OutputLimitReached,
    bool HasMoreAfter,
    int? NextPage,
    IReadOnlyList<PdfTextPageResult> Pages);

public sealed record PdfWorkerRequest(
    string FullPath,
    int StartPage,
    int MaxPages);

public sealed record PdfWorkerResponse(
    string Status,
    int PageCount = 0,
    int StartPage = 0,
    int EndPage = 0,
    int TotalTextLength = 0,
    bool OutputLimitReached = false,
    bool HasMoreAfter = false,
    int? NextPage = null,
    IReadOnlyList<PdfTextPageResult>? Pages = null);

public interface IVisualFilesProvider
{
    Task<LoomResult<VisualImageResult>> ReadImageAsync(
        VisualImageRequest request,
        CancellationToken cancellationToken);

    Task<LoomResult<PdfTextReadResult>> ReadPdfTextAsync(
        PdfTextReadRequest request,
        CancellationToken cancellationToken);
}
