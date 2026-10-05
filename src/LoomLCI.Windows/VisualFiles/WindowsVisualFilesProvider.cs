using System.Buffers.Binary;
using LoomLCI.Core;
using LoomLCI.Core.VisualFiles;

namespace LoomLCI.Windows.VisualFiles;

public sealed class WindowsVisualFilesProvider : IVisualFilesProvider
{
    private readonly PdfWorkerClient? _pdfWorker;

    public WindowsVisualFilesProvider()
    {
    }

    public WindowsVisualFilesProvider(PdfWorkerLaunchDescriptor pdfWorkerLaunch)
    {
        _pdfWorker = new PdfWorkerClient(pdfWorkerLaunch);
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

            var mimeType = DetectMimeType(bytes);
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

    private static string? DetectMimeType(ReadOnlySpan<byte> bytes)
        => LooksLikePng(bytes) ? "image/png"
            : LooksLikeJpeg(bytes) ? "image/jpeg"
            : LooksLikeWebP(bytes) ? "image/webp"
            : null;

    private static bool LooksLikePng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature =
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 45 || !bytes[..8].SequenceEqual(signature))
        {
            return false;
        }

        var offset = 8;
        var firstChunk = true;
        while (offset + 12 <= bytes.Length)
        {
            var chunkLength =
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            var chunkTotal = 12L + chunkLength;
            if (chunkTotal > bytes.Length - offset)
            {
                return false;
            }

            var chunkType = bytes.Slice(offset + 4, 4);
            if (firstChunk)
            {
                if (chunkLength != 13 || !chunkType.SequenceEqual("IHDR"u8))
                {
                    return false;
                }

                var width =
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4));
                var height =
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 12, 4));
                if (width == 0 || height == 0)
                {
                    return false;
                }

                firstChunk = false;
            }

            if (chunkType.SequenceEqual("IEND"u8))
            {
                return chunkLength == 0 && offset + chunkTotal == bytes.Length;
            }

            offset += (int)chunkTotal;
        }

        return false;
    }

    private static bool LooksLikeJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 ||
            bytes[0] != 0xFF ||
            bytes[1] != 0xD8 ||
            bytes[^2] != 0xFF ||
            bytes[^1] != 0xD9)
        {
            return false;
        }

        var offset = 2;
        var sawStartOfFrame = false;

        while (offset < bytes.Length - 2)
        {
            if (bytes[offset] != 0xFF)
            {
                return false;
            }

            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= bytes.Length)
            {
                return false;
            }

            var marker = bytes[offset++];
            if (marker == 0x00 || marker == 0xD8 || marker == 0xD9)
            {
                return false;
            }

            if (marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
            {
                continue;
            }

            if (offset + 2 > bytes.Length)
            {
                return false;
            }

            var segmentLength =
                BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
            {
                return false;
            }

            if (IsStartOfFrameMarker(marker))
            {
                sawStartOfFrame = true;
            }

            if (marker == 0xDA)
            {
                return sawStartOfFrame;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrameMarker(byte marker)
        => marker is >= 0xC0 and <= 0xCF &&
           marker is not 0xC4 and not 0xC8 and not 0xCC;

    private static bool LooksLikeWebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 ||
            !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        var declaredLength =
            (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8L;
        if (declaredLength != bytes.Length)
        {
            return false;
        }

        var offset = 12;
        var firstChunk = true;

        while (offset + 8 <= bytes.Length)
        {
            var chunkType = bytes.Slice(offset, 4);
            var chunkLength =
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var paddedLength = (long)chunkLength + (chunkLength & 1);
            var totalLength = 8L + paddedLength;

            if (totalLength > bytes.Length - offset)
            {
                return false;
            }

            if (firstChunk)
            {
                if (!chunkType.SequenceEqual("VP8 "u8) &&
                    !chunkType.SequenceEqual("VP8L"u8) &&
                    !chunkType.SequenceEqual("VP8X"u8))
                {
                    return false;
                }

                firstChunk = false;
            }

            offset += (int)totalLength;
        }

        return !firstChunk && offset == bytes.Length;
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
