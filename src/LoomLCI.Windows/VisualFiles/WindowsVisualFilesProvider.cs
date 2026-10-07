using LoomLCI.Core;
using LoomLCI.Core.VisualFiles;

namespace LoomLCI.Windows.VisualFiles;

public sealed class WindowsVisualFilesProvider : IVisualFilesProvider
{
    private readonly PdfWorkerClient? _pdfWorker;
    private readonly PdfRenderWorkerClient? _pdfRenderWorker;

    public WindowsVisualFilesProvider()
    {
    }

    public WindowsVisualFilesProvider(PdfWorkerLaunchDescriptor pdfWorkerLaunch)
    {
        _pdfWorker = new PdfWorkerClient(pdfWorkerLaunch);
        _pdfRenderWorker = new PdfRenderWorkerClient(pdfWorkerLaunch);
    }

    public async Task<LoomResult<VisualImageResult>> ReadImageAsync(
        VisualImageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(request.FullPath))
            {
                return LoomResult<VisualImageResult>.Failure(
                    LoomErrors.NotFound($"File '{request.FullPath}' was not found."));
            }

            await using var stream = new FileStream(
                request.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length > VisualFilesLimits.MaxImageBytes)
            {
                return LoomResult<VisualImageResult>.Failure(
                    Unsupported(
                        $"Image '{request.FullPath}' exceeds the 6 MiB image limit.",
                        "image_too_large"));
            }

            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

            var mimeType = VisualImageFormat.DetectMimeType(bytes);
            if (mimeType is null)
            {
                return LoomResult<VisualImageResult>.Failure(
                    Unsupported(
                        $"File '{request.FullPath}' is not a supported PNG, JPEG, or WebP image.",
                        "unsupported_image_format"));
            }

            return LoomResult<VisualImageResult>.Success(
                new VisualImageResult(
                    request.RequestedPath,
                    request.FullPath,
                    mimeType,
                    bytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return LoomResult<VisualImageResult>.Failure(
                new LoomError(
                    "busy",
                    $"File '{request.FullPath}' is currently open for writing. Retry when it is stable.",
                    true,
                    new Dictionary<string, object?> { ["reason"] = "file_busy" }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<VisualImageResult>.Failure(LoomErrors.AccessDenied(ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            return LoomResult<VisualImageResult>.Failure(LoomErrors.NotFound(ex.Message));
        }
        catch (DirectoryNotFoundException ex)
        {
            return LoomResult<VisualImageResult>.Failure(LoomErrors.NotFound(ex.Message));
        }
        catch (IOException ex)
        {
            return LoomResult<VisualImageResult>.Failure(LoomErrors.ExecutionFailed(ex.Message));
        }
    }

    public async Task<LoomResult<PdfTextReadResult>> ReadPdfTextAsync(
        PdfTextReadRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(request.FullPath))
            {
                return LoomResult<PdfTextReadResult>.Failure(
                    LoomErrors.NotFound($"File '{request.FullPath}' was not found."));
            }

            await using var stableHandle = new FileStream(
                request.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stableHandle.Length > VisualFilesLimits.MaxPdfBytes)
            {
                return LoomResult<PdfTextReadResult>.Failure(
                    Unsupported(
                        $"PDF '{request.FullPath}' exceeds the 64 MiB PDF limit.",
                        "pdf_too_large"));
            }

            if (_pdfWorker is null)
            {
                return LoomResult<PdfTextReadResult>.Failure(
                    LoomErrors.ExecutionFailed("PDF worker carrier is not configured."));
            }

            return await _pdfWorker.ReadAsync(
                    request,
                    stableHandle.Length,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return LoomResult<PdfTextReadResult>.Failure(
                new LoomError(
                    "busy",
                    $"File '{request.FullPath}' is currently open for writing. Retry when it is stable.",
                    true,
                    new Dictionary<string, object?> { ["reason"] = "file_busy" }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<PdfTextReadResult>.Failure(LoomErrors.AccessDenied(ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            return LoomResult<PdfTextReadResult>.Failure(LoomErrors.NotFound(ex.Message));
        }
        catch (DirectoryNotFoundException ex)
        {
            return LoomResult<PdfTextReadResult>.Failure(LoomErrors.NotFound(ex.Message));
        }
        catch (IOException ex)
        {
            return LoomResult<PdfTextReadResult>.Failure(LoomErrors.ExecutionFailed(ex.Message));
        }
    }

    public async Task<LoomResult<PdfPageRenderResult>> RenderPdfPageAsync(
        PdfPageRenderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(request.FullPath))
            {
                return LoomResult<PdfPageRenderResult>.Failure(
                    LoomErrors.NotFound($"File '{request.FullPath}' was not found."));
            }

            await using var stableHandle = new FileStream(
                request.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            if (stableHandle.Length > VisualFilesLimits.MaxPdfBytes)
            {
                return LoomResult<PdfPageRenderResult>.Failure(
                    Unsupported(
                        $"PDF '{request.FullPath}' exceeds the 64 MiB PDF limit.",
                        "pdf_too_large"));
            }

            if (_pdfRenderWorker is null)
            {
                return LoomResult<PdfPageRenderResult>.Failure(
                    LoomErrors.ExecutionFailed("PDF render worker carrier is not configured."));
            }

            return await _pdfRenderWorker.RenderAsync(
                    request,
                    stableHandle.Length,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return LoomResult<PdfPageRenderResult>.Failure(
                new LoomError(
                    "busy",
                    $"File '{request.FullPath}' is currently open for writing. Retry when it is stable.",
                    true,
                    new Dictionary<string, object?> { ["reason"] = "file_busy" }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<PdfPageRenderResult>.Failure(LoomErrors.AccessDenied(ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            return LoomResult<PdfPageRenderResult>.Failure(LoomErrors.NotFound(ex.Message));
        }
        catch (DirectoryNotFoundException ex)
        {
            return LoomResult<PdfPageRenderResult>.Failure(LoomErrors.NotFound(ex.Message));
        }
        catch (IOException ex)
        {
            return LoomResult<PdfPageRenderResult>.Failure(LoomErrors.ExecutionFailed(ex.Message));
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var win32Code = ex.HResult & 0xFFFF;
        return win32Code is 32 or 33;
    }

    private static LoomError Unsupported(string message, string reason)
        => new(
            "unsupported",
            message,
            false,
            new Dictionary<string, object?> { ["reason"] = reason });
}
