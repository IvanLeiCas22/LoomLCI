using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using LoomLCI.Core;
using LoomLCI.Core.VisualFiles;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public sealed record VisualImageDto(
    string RequestedPath,
    string FullPath,
    string MimeType,
    long SizeBytes);

public sealed record PdfTextPageDto(
    int PageNumber,
    string Text,
    int TextLength,
    bool TextTruncated);

public sealed record PdfTextReadDto(
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
    IReadOnlyList<PdfTextPageDto> Pages);

[McpServerToolType]
public sealed class VisualFilesTools
{
    private readonly VisualFilesCapability _visualFiles;

    public VisualFilesTools(VisualFilesCapability visualFiles)
    {
        _visualFiles = visualFiles;
    }

    [McpServerTool(
        Name = "filesystem_view_image",
        Title = "View local image",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<VisualImageDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Reads one known local PNG, JPEG, or WebP file and returns it as image content for visual inspection. " +
        "Use this when the model needs to see an image rather than read it as text or base64. " +
        "The path may be absolute or relative to a work session base directory. " +
        "The file is returned unchanged: this tool does not resize, convert, edit, or OCR the image. " +
        "Images over 6 MiB or whose MCP result would exceed the visual payload budget are rejected.")]
    public async Task<CallToolResult> ViewImage(
        [Description("Image path. May be absolute or relative to the work session base directory.")]
        string path,
        [Description("Optional work session handle used to resolve relative paths.")]
        string? workId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _visualFiles.ViewImageAsync(
            path,
            string.IsNullOrWhiteSpace(workId) ? null : new WorkId(workId),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return McpToolResults.From(
                ToolEnvelope<VisualImageDto>.From(
                    LoomResult<VisualImageDto>.Failure(result.Error!)));
        }

        var image = result.Value!;
        var envelope = ToolEnvelope<VisualImageDto>.From(
            LoomResult<VisualImageDto>.Success(
                new VisualImageDto(
                    image.RequestedPath,
                    image.FullPath,
                    image.MimeType,
                    image.Bytes.LongLength)));

        if (!McpVisualPayloadLimits.IsWithinLimits(
                envelope,
                image.Bytes,
                image.MimeType,
                out var estimatedBytes))
        {
            return McpToolResults.From(
                ToolEnvelope<VisualImageDto>.From(
                    LoomResult<VisualImageDto>.Failure(
                        new LoomError(
                            "unsupported",
                            "The image would exceed the MCP visual payload budget.",
                            false,
                            new Dictionary<string, object?>
                            {
                                ["reason"] = "visual_payload_too_large",
                                ["estimatedCallToolResultBytes"] = estimatedBytes,
                                ["maxCallToolResultBytes"] =
                                    McpVisualPayloadLimits.MaxVisualCallToolResultBytes
                            }))));
        }

        return McpToolResults.From(
            envelope,
            ImageContentBlock.FromBytes(image.Bytes, image.MimeType));
    }

    [McpServerTool(
        Name = "filesystem_read_pdf",
        Title = "Read PDF text",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<PdfTextReadDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Extracts bounded text from a known local PDF using a crash-isolated worker. " +
        "Pages are 1-based and results are paginated by page. PDFs over 64 MiB are rejected. " +
        "This tool does not perform OCR or render pages, so scanned/image-only PDFs may return little or no text.")]
    public async Task<CallToolResult> ReadPdf(
        [Description("PDF path. May be absolute or relative to the work session base directory.")]
        string path,
        [Description("Optional work session handle used to resolve relative paths.")]
        string? workId = null,
        [Description("1-based first page to read.")][Range(1, int.MaxValue)]
        int startPage = 1,
        [Description("Maximum number of pages to read in this call.")]
        [Range(1, VisualFilesLimits.MaxPdfPagesPerRead)]
        int maxPages = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _visualFiles.ReadPdfTextAsync(
            path,
            string.IsNullOrWhiteSpace(workId) ? null : new WorkId(workId),
            startPage,
            maxPages,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return McpToolResults.From(
                ToolEnvelope<PdfTextReadDto>.From(
                    LoomResult<PdfTextReadDto>.Failure(result.Error!)));
        }

        var pdf = result.Value!;
        var dto = new PdfTextReadDto(
            pdf.RequestedPath,
            pdf.FullPath,
            pdf.SizeBytes,
            pdf.PageCount,
            pdf.StartPage,
            pdf.EndPage,
            pdf.TotalTextLength,
            pdf.OutputLimitReached,
            pdf.HasMoreAfter,
            pdf.NextPage,
            pdf.Pages.Select(page => new PdfTextPageDto(
                page.PageNumber,
                page.Text,
                page.TextLength,
                page.TextTruncated)).ToArray());

        return McpToolResults.From(
            ToolEnvelope<PdfTextReadDto>.From(
                LoomResult<PdfTextReadDto>.Success(dto)));
    }
}
