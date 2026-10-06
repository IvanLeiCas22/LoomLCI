using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using LoomLCI.Core.VisualFiles;
using LoomLCI.Windows.Processes;

namespace LoomLCI.Windows.VisualFiles;

internal sealed class PdfRenderWorkerClient
{
    private const long ProcessMemoryLimitBytes = 256L * 1024 * 1024;
    private const int MaxResponseUtf8Bytes = 9 * 1024 * 1024;
    private const int MaxDiagnosticUtf8Bytes = 64 * 1024;
    private const int MaxCapturedChars = 9_600_000;
    private static readonly TimeSpan DefaultWorkerTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly PdfWorkerLaunchDescriptor _launch;
    private readonly TimeSpan _workerTimeout;
    private readonly long _processMemoryLimitBytes;

    public PdfRenderWorkerClient(
        PdfWorkerLaunchDescriptor launch,
        TimeSpan? workerTimeout = null,
        long? processMemoryLimitBytes = null)
    {
        _launch = launch;
        _workerTimeout = workerTimeout ?? DefaultWorkerTimeout;
        _processMemoryLimitBytes = processMemoryLimitBytes ?? ProcessMemoryLimitBytes;

        if (_workerTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(workerTimeout));
        }

        if (_processMemoryLimitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processMemoryLimitBytes));
        }
    }

    public async Task<LoomResult<PdfPageRenderResult>> RenderAsync(
        PdfPageRenderRequest request,
        long pdfSizeBytes,
        CancellationToken cancellationToken)
    {
        WindowsProcessResource? process = null;
        try
        {
            var arguments = _launch.ArgumentsPrefix
                .Concat(new[] { "--internal-pdf-render-worker-v1" })
                .ToArray();
            process = WindowsNativeProcessLauncher.Launch(
                new ProcessLaunchSpec(
                    _launch.Executable,
                    arguments,
                    _launch.WorkingDirectory,
                    new Dictionary<string, string?>(),
                    ProcessIoMode.Pipes,
                    null,
                    null),
                processMemoryLimitBytes: _processMemoryLimitBytes);

            var requestJson = JsonSerializer.Serialize(
                new PdfRenderWorkerRequest(
                    request.FullPath,
                    request.Page,
                    request.MaxWidth,
                    request.MaxHeight),
                JsonOptions);
            var write = await process.WriteAsync(
                    requestJson + "\n",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return LoomResult<PdfPageRenderResult>.Failure(write.Error!);
            }

            using var deadline = new CancellationTokenSource(_workerTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);

            try
            {
                await process.WaitForExitAndOutputAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "deadline_exceeded",
                        $"PDF rendering exceeded the {_workerTimeout.TotalSeconds:0.###} second worker deadline.",
                        retryable: true,
                        "pdf_render_worker_timeout"));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = process.Snapshot(new ProcessHandle("proc_pdf_render_worker"));
            var read = process.Read(
                new ProcessHandle("proc_pdf_render_worker"),
                0,
                0,
                0,
                MaxCapturedChars);
            var stdout = Concat(read.Stdout);
            var stderr = Concat(read.Stderr);

            if (snapshot.ExitCode != 0)
            {
                return Crashed(
                    $"PDF render worker exited with code {snapshot.ExitCode?.ToString() ?? "unknown"}.",
                    stderr);
            }

            if (read.Stdout is { ObservedUntilCursor: > MaxCapturedChars })
            {
                return Crashed("PDF render worker response exceeded its capture limit.", stderr);
            }

            int responseBytes;
            try
            {
                responseBytes = Encoding.UTF8.GetByteCount(stdout);
            }
            catch (EncoderFallbackException)
            {
                return Crashed("PDF render worker response was not valid Unicode.", stderr);
            }

            if (responseBytes > MaxResponseUtf8Bytes)
            {
                return Crashed("PDF render worker response exceeded the 9 MiB protocol limit.", stderr);
            }

            PdfRenderWorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<PdfRenderWorkerResponse>(
                    stdout.Trim(),
                    JsonOptions);
            }
            catch (JsonException)
            {
                return Crashed("PDF render worker returned invalid JSON.", stderr);
            }

            if (response is null)
            {
                return Crashed("PDF render worker returned an empty response.", stderr);
            }

            return MapResponse(request, pdfSizeBytes, response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return LoomResult<PdfPageRenderResult>.Failure(LoomErrors.AccessDenied(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<PdfPageRenderResult>.Failure(LoomErrors.AccessDenied(ex.Message));
        }
        catch (Exception ex) when (
            ex is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            return LoomResult<PdfPageRenderResult>.Failure(
                Error(
                    "execution_failed",
                    $"Could not run PDF render worker: {ex.Message}",
                    retryable: false,
                    "pdf_render_worker_crashed"));
        }
        finally
        {
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static LoomResult<PdfPageRenderResult> MapResponse(
        PdfPageRenderRequest request,
        long pdfSizeBytes,
        PdfRenderWorkerResponse response)
    {
        switch (response.Status)
        {
            case "ok":
                if (response.PageCount < 1 ||
                    response.Page != request.Page ||
                    response.Width < 1 ||
                    response.Height < 1 ||
                    response.Width > request.MaxWidth ||
                    response.Height > request.MaxHeight ||
                    response.PngBytes is null ||
                    response.PngBytes.Length == 0 ||
                    response.PngBytes.Length > VisualFilesLimits.MaxImageBytes)
                {
                    return Crashed("PDF render worker returned inconsistent result metadata.", null);
                }

                return LoomResult<PdfPageRenderResult>.Success(
                    new PdfPageRenderResult(
                        request.RequestedPath,
                        request.FullPath,
                        pdfSizeBytes,
                        response.Page,
                        response.PageCount,
                        response.Width,
                        response.Height,
                        "image/png",
                        response.PngBytes));

            case "password_protected":
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "unsupported",
                        "The PDF is password protected and passwords are not supported by this tool.",
                        retryable: false,
                        "password_protected_pdf"));

            case "unsupported_security":
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "unsupported",
                        "The PDF uses a security mode that this renderer does not support.",
                        retryable: false,
                        "unsupported_pdf_security"));

            case "invalid_pdf":
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "unsupported",
                        "The file is not a valid supported PDF.",
                        retryable: false,
                        "invalid_pdf"));

            case "page_out_of_range":
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "invalid_argument",
                        $"page {request.Page} is outside the PDF page range 1..{response.PageCount}.",
                        retryable: false,
                        "page_out_of_range"));

            case "rendered_image_too_large":
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "unsupported",
                        "The rendered PNG exceeds the visual payload limit. Retry with smaller maxWidth/maxHeight values.",
                        retryable: false,
                        "rendered_image_too_large"));

            case "resource_limit":
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "unsupported",
                        "PDF rendering exceeded the worker memory limit.",
                        retryable: false,
                        "pdf_render_resource_limit"));

            case "render_failed":
                return LoomResult<PdfPageRenderResult>.Failure(
                    Error(
                        "execution_failed",
                        "PDF rendering failed.",
                        retryable: false,
                        "pdf_render_failed"));

            default:
                return Crashed(
                    $"PDF render worker returned unknown status '{response.Status}'.",
                    null);
        }
    }

    private static LoomResult<PdfPageRenderResult> Crashed(
        string message,
        string? diagnostics)
    {
        var suffix = BoundDiagnostics(diagnostics);
        return LoomResult<PdfPageRenderResult>.Failure(
            Error(
                "execution_failed",
                string.IsNullOrWhiteSpace(suffix)
                    ? message
                    : $"{message} Diagnostics: {suffix}",
                retryable: false,
                "pdf_render_worker_crashed"));
    }

    private static LoomError Error(
        string code,
        string message,
        bool retryable,
        string reason)
        => new(
            code,
            message,
            retryable,
            new Dictionary<string, object?> { ["reason"] = reason });

    private static string Concat(OutputStreamReadResult? stream)
        => stream is null
            ? string.Empty
            : string.Concat(stream.Chunks.Select(chunk => chunk.Text));

    private static string? BoundDiagnostics(string? diagnostics)
    {
        if (string.IsNullOrWhiteSpace(diagnostics))
        {
            return null;
        }

        var value = diagnostics.Trim();
        while (Encoding.UTF8.GetByteCount(value) > MaxDiagnosticUtf8Bytes && value.Length > 1)
        {
            value = value[..(value.Length / 2)];
        }

        return value;
    }
}
