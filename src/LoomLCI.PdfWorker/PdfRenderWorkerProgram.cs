using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LoomLCI.Core.VisualFiles;
using StbImageWriteSharp;

namespace LoomLCI.PdfWorker;

public static class PdfRenderWorkerProgram
{
    private const int MaxRequestChars = 1 * 1024 * 1024;
    private const int MaxResponseUtf8Bytes = 9 * 1024 * 1024;
    private const int FpdfErrFile = 2;
    private const int FpdfErrFormat = 3;
    private const int FpdfErrPassword = 4;
    private const int FpdfErrSecurity = 5;
    private const int FpdfAnnot = 0x01;
    private const int FpdfReverseByteOrder = 0x10;
    private const int FpdfRenderLimitedImageCache = 0x200;
    private const int FpdfBitmapBgra = 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
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

    private static async Task<PdfRenderWorkerRequest> ReadRequestAsync(
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
            ?? throw new InvalidDataException("PDF render worker request was empty.");
        if (line.Length > MaxRequestChars)
        {
            throw new InvalidDataException("PDF render worker request exceeded its size limit.");
        }

        var request = JsonSerializer.Deserialize<PdfRenderWorkerRequest>(line, JsonOptions)
            ?? throw new InvalidDataException("PDF render worker request was invalid.");

        if (string.IsNullOrWhiteSpace(request.FullPath) ||
            request.Page < 1 ||
            request.MaxWidth is < VisualFilesLimits.MinPdfRenderDimension
                or > VisualFilesLimits.MaxPdfRenderDimension ||
            request.MaxHeight is < VisualFilesLimits.MinPdfRenderDimension
                or > VisualFilesLimits.MaxPdfRenderDimension)
        {
            throw new InvalidDataException("PDF render worker request contained invalid values.");
        }

        return request;
    }

    private static PdfRenderWorkerResponse Process(PdfRenderWorkerRequest request)
    {
        NativePdfium.FPDF_InitLibrary();
        NativePdfium.FPDF_SetSandBoxPolicy(0, 0);

        try
        {
            var document = NativePdfium.FPDF_LoadDocument(request.FullPath, IntPtr.Zero);
            if (document == IntPtr.Zero)
            {
                return MapLoadError(Marshal.GetLastPInvokeError());
            }

            try
            {
                var pageCount = NativePdfium.FPDF_GetPageCount(document);
                if (pageCount < 1)
                {
                    return new PdfRenderWorkerResponse("invalid_pdf");
                }

                if (request.Page > pageCount)
                {
                    return new PdfRenderWorkerResponse(
                        "page_out_of_range",
                        PageCount: pageCount,
                        Page: request.Page);
                }

                var page = NativePdfium.FPDF_LoadPage(document, request.Page - 1);
                if (page == IntPtr.Zero)
                {
                    return new PdfRenderWorkerResponse("invalid_pdf", PageCount: pageCount);
                }

                try
                {
                    var pageWidth = NativePdfium.FPDF_GetPageWidth(page);
                    var pageHeight = NativePdfium.FPDF_GetPageHeight(page);
                    if (!double.IsFinite(pageWidth) ||
                        !double.IsFinite(pageHeight) ||
                        pageWidth <= 0 ||
                        pageHeight <= 0)
                    {
                        return new PdfRenderWorkerResponse("invalid_pdf", PageCount: pageCount);
                    }

                    var scale = Math.Min(
                        request.MaxWidth / pageWidth,
                        request.MaxHeight / pageHeight);
                    var width = Math.Max(1, (int)Math.Floor(pageWidth * scale));
                    var height = Math.Max(1, (int)Math.Floor(pageHeight * scale));

                    var bitmap = NativePdfium.FPDFBitmap_CreateEx(
                        width,
                        height,
                        FpdfBitmapBgra,
                        IntPtr.Zero,
                        0);
                    if (bitmap == IntPtr.Zero)
                    {
                        return new PdfRenderWorkerResponse("render_failed", PageCount: pageCount);
                    }

                    try
                    {
                        _ = NativePdfium.FPDFBitmap_FillRect(
                            bitmap,
                            0,
                            0,
                            width,
                            height,
                            0xFFFFFFFF);

                        NativePdfium.FPDF_RenderPageBitmap(
                            bitmap,
                            page,
                            0,
                            0,
                            width,
                            height,
                            0,
                            FpdfAnnot |
                            FpdfReverseByteOrder |
                            FpdfRenderLimitedImageCache);

                        var stride = NativePdfium.FPDFBitmap_GetStride(bitmap);
                        var buffer = NativePdfium.FPDFBitmap_GetBuffer(bitmap);
                        if (buffer == IntPtr.Zero || stride != checked(width * 4))
                        {
                            return new PdfRenderWorkerResponse("render_failed", PageCount: pageCount);
                        }

                        using var png = new MemoryStream();
                        unsafe
                        {
                            new ImageWriter().WritePng(
                                buffer.ToPointer(),
                                width,
                                height,
                                ColorComponents.RedGreenBlueAlpha,
                                png);
                        }

                        if (png.Length > VisualFilesLimits.MaxImageBytes)
                        {
                            return new PdfRenderWorkerResponse(
                                "rendered_image_too_large",
                                pageCount,
                                request.Page,
                                width,
                                height);
                        }

                        return new PdfRenderWorkerResponse(
                            "ok",
                            pageCount,
                            request.Page,
                            width,
                            height,
                            png.ToArray());
                    }
                    finally
                    {
                        NativePdfium.FPDFBitmap_Destroy(bitmap);
                    }
                }
                finally
                {
                    NativePdfium.FPDF_ClosePage(page);
                }
            }
            finally
            {
                NativePdfium.FPDF_CloseDocument(document);
            }
        }
        finally
        {
            NativePdfium.FPDF_DestroyLibrary();
        }
    }

    private static PdfRenderWorkerResponse MapLoadError(int error)
        => error switch
        {
            FpdfErrPassword => new PdfRenderWorkerResponse("password_protected"),
            FpdfErrSecurity => new PdfRenderWorkerResponse("unsupported_security"),
            FpdfErrFile or FpdfErrFormat => new PdfRenderWorkerResponse("invalid_pdf"),
            _ => new PdfRenderWorkerResponse("render_failed")
        };

    private static async Task WriteResponseAsync(
        Stream output,
        PdfRenderWorkerResponse response,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        if (bytes.Length > MaxResponseUtf8Bytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                new PdfRenderWorkerResponse("rendered_image_too_large"),
                JsonOptions);
        }

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

internal static class NativePdfium
{
    private const string Pdfium = "pdfium";

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern void FPDF_InitLibrary();

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern void FPDF_DestroyLibrary();

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern void FPDF_SetSandBoxPolicy(uint policy, int enable);

    [DllImport(Pdfium, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        IntPtr password);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern double FPDF_GetPageWidth(IntPtr page);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern double FPDF_GetPageHeight(IntPtr page);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern IntPtr FPDFBitmap_CreateEx(
        int width,
        int height,
        int format,
        IntPtr firstScan,
        int stride);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern int FPDFBitmap_FillRect(
        IntPtr bitmap,
        int left,
        int top,
        int width,
        int height,
        uint color);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(Pdfium, ExactSpelling = true)]
    internal static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);
}
