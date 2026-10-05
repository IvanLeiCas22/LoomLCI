using System.Text;
using System.Text.Json;
using LoomLCI.Core.VisualFiles;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace LoomLCI.PdfWorker;

public static class PdfWorkerProgram
{
    private const int MaxRequestChars = 1 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] ResourceLimitFrame =
        "{\"status\":\"resource_limit\"}\n"u8.ToArray();

    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        Stream error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = await ReadRequestAsync(input, cancellationToken).ConfigureAwait(false);
            var response = Process(request);
            await WriteResponseAsync(output, response, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OutOfMemoryException)
        {
            try
            {
                await output.WriteAsync(ResourceLimitFrame, CancellationToken.None).ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                return 0;
            }
            catch
            {
                return 70;
            }
        }
        catch (Exception ex)
        {
            await WriteDiagnosticAsync(error, ex).ConfigureAwait(false);
            return 70;
        }
    }

    private static async Task<PdfWorkerRequest> ReadRequestAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            input,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("PDF worker request was empty.");
        if (line.Length > MaxRequestChars)
        {
            throw new InvalidDataException("PDF worker request exceeded its size limit.");
        }

        var request = JsonSerializer.Deserialize<PdfWorkerRequest>(line, JsonOptions)
            ?? throw new InvalidDataException("PDF worker request was invalid.");
        if (string.IsNullOrWhiteSpace(request.FullPath) ||
            request.StartPage < 1 ||
            request.MaxPages is < 1 or > VisualFilesLimits.MaxPdfPagesPerRead)
        {
            throw new InvalidDataException("PDF worker request contained invalid values.");
        }

        return request;
    }

    private static PdfWorkerResponse Process(PdfWorkerRequest request)
    {
        try
        {
            using var stream = new FileStream(
                request.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.SequentialScan);
            using var document = PdfDocument.Open(stream);

            var pageCount = document.NumberOfPages;
            if (request.StartPage > pageCount)
            {
                return new PdfWorkerResponse(
                    "page_out_of_range",
                    PageCount: pageCount,
                    StartPage: request.StartPage);
            }

            var pages = new List<PdfTextPageResult>();
            var totalTextLength = 0;
            var outputLimitReached = false;
            int? nextPage = null;
            var lastRequestedPage = Math.Min(
                pageCount,
                checked(request.StartPage + request.MaxPages - 1));

            for (var pageNumber = request.StartPage;
                 pageNumber <= lastRequestedPage;
                 pageNumber++)
            {
                var page = document.GetPage(pageNumber);
                var text = ContentOrderTextExtractor.GetText(page);
                var bounded = TruncateByCodePoints(
                    text,
                    VisualFilesLimits.MaxPageTextCodePoints);

                if (totalTextLength + bounded.CodePoints >
                    VisualFilesLimits.MaxTotalTextCodePoints)
                {
                    outputLimitReached = true;
                    nextPage = pageNumber;
                    break;
                }

                pages.Add(new PdfTextPageResult(
                    pageNumber,
                    bounded.Text,
                    bounded.CodePoints,
                    bounded.Truncated));
                outputLimitReached |= bounded.Truncated;
                totalTextLength += bounded.CodePoints;
            }

            var endPage = pages.Count == 0
                ? request.StartPage - 1
                : pages[^1].PageNumber;
            var hasMoreAfter = nextPage.HasValue || endPage < pageCount;
            nextPage ??= hasMoreAfter ? endPage + 1 : null;

            return new PdfWorkerResponse(
                "ok",
                pageCount,
                request.StartPage,
                endPage,
                totalTextLength,
                outputLimitReached,
                hasMoreAfter,
                nextPage,
                pages);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new PdfWorkerResponse("password_protected");
        }
        catch (PdfDocumentFormatException)
        {
            return new PdfWorkerResponse("invalid_pdf");
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception)
        {
            return new PdfWorkerResponse("invalid_pdf");
        }
    }

    internal static (string Text, int CodePoints, bool Truncated) TruncateByCodePoints(
        string text,
        int maxCodePoints)
    {
        var count = 0;
        var charLength = 0;
        var truncated = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (count == maxCodePoints)
            {
                truncated = true;
                break;
            }

            charLength += rune.Utf16SequenceLength;
            count++;
        }

        return truncated
            ? (text[..charLength], count, true)
            : (text, count, false);
    }

    private static async Task WriteResponseAsync(
        Stream output,
        PdfWorkerResponse response,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDiagnosticAsync(Stream error, Exception exception)
    {
        try
        {
            var message = $"{exception.GetType().Name}: {exception.Message}";
            if (message.Length > 4096)
            {
                message = message[..4096];
            }

            var bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);
            await error.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
