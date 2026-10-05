using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using LoomLCI.Core.VisualFiles;
using LoomLCI.Windows.Processes;

namespace LoomLCI.Windows.VisualFiles;

public sealed record PdfWorkerLaunchDescriptor(
    string Executable,
    IReadOnlyList<string> ArgumentsPrefix,
    string WorkingDirectory);

internal sealed class PdfWorkerClient
{
    private const long ProcessMemoryLimitBytes = 256L * 1024 * 1024;
    private const int MaxResponseUtf8Bytes = 4 * 1024 * 1024;
    private const int MaxDiagnosticUtf8Bytes = 64 * 1024;
    private const int MaxCapturedChars = 4_400_000;
    private static readonly TimeSpan DefaultWorkerTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly PdfWorkerLaunchDescriptor _launch;
    private readonly TimeSpan _workerTimeout;
    private readonly long _processMemoryLimitBytes;

    public PdfWorkerClient(
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

    public async Task<LoomResult<PdfTextReadResult>> ReadAsync(
        PdfTextReadRequest request,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        WindowsProcessResource? process = null;
        try
        {
            var arguments = _launch.ArgumentsPrefix
                .Concat(new[] { "--internal-pdf-worker-v1" })
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
                new PdfWorkerRequest(
                    request.FullPath,
                    request.StartPage,
                    request.MaxPages),
                JsonOptions);
            var write = await process.WriteAsync(
                    requestJson + "\n",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return LoomResult<PdfTextReadResult>.Failure(write.Error!);
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
                return LoomResult<PdfTextReadResult>.Failure(
                    Error(
                        "deadline_exceeded",
                        $"PDF text extraction exceeded the {_workerTimeout.TotalSeconds:0.###} second worker deadline.",
                        retryable: true,
                        "pdf_worker_timeout"));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = process.Snapshot(new ProcessHandle("proc_pdf_worker"));
            var read = process.Read(
                new ProcessHandle("proc_pdf_worker"),
                0,
                0,
                0,
                MaxCapturedChars);
            var stdout = Concat(read.Stdout);
            var stderr = Concat(read.Stderr);

            if (snapshot.ExitCode != 0)
            {
                return Crashed(
                    $"PDF worker exited with code {snapshot.ExitCode?.ToString() ?? "unknown"}.",
                    stderr);
            }

            if (read.Stdout is { ObservedUntilCursor: > MaxCapturedChars })
            {
                return Crashed("PDF worker response exceeded its capture limit.", stderr);
            }

            int responseBytes;
            try
            {
                responseBytes = Encoding.UTF8.GetByteCount(stdout);
            }
            catch (EncoderFallbackException)
            {
                return Crashed("PDF worker response was not valid Unicode.", stderr);
            }

            if (responseBytes > MaxResponseUtf8Bytes)
            {
                return Crashed("PDF worker response exceeded the 4 MiB protocol limit.", stderr);
            }

            PdfWorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<PdfWorkerResponse>(stdout.Trim(), JsonOptions);
            }
            catch (JsonException)
            {
                return Crashed("PDF worker returned invalid JSON.", stderr);
            }

            if (response is null)
            {
                return Crashed("PDF worker returned an empty response.", stderr);
            }

            return MapResponse(request, sizeBytes, response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return LoomResult<PdfTextReadResult>.Failure(LoomErrors.AccessDenied(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<PdfTextReadResult>.Failure(LoomErrors.AccessDenied(ex.Message));
        }
        catch (Exception ex) when (
            ex is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            return LoomResult<PdfTextReadResult>.Failure(
                Error(
                    "execution_failed",
                    $"Could not run PDF worker: {ex.Message}",
                    retryable: false,
                    "pdf_worker_crashed"));
        }
        finally
        {
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static LoomResult<PdfTextReadResult> MapResponse(
        PdfTextReadRequest request,
        long sizeBytes,
        PdfWorkerResponse response)
    {
        switch (response.Status)
        {
            case "ok":
            {
                var pages = response.Pages ?? Array.Empty<PdfTextPageResult>();
                if (response.PageCount < 1 ||
                    response.StartPage != request.StartPage ||
                    response.EndPage < request.StartPage - 1 ||
                    pages.Any(page => page.PageNumber < request.StartPage ||
                                      page.TextLength < 0 ||
                                      page.TextLength > VisualFilesLimits.MaxPageTextCodePoints) ||
                    response.TotalTextLength < 0 ||
                    response.TotalTextLength > VisualFilesLimits.MaxTotalTextCodePoints)
                {
                    return Crashed("PDF worker returned inconsistent result metadata.", null);
                }

                return LoomResult<PdfTextReadResult>.Success(
                    new PdfTextReadResult(
                        request.RequestedPath,
                        request.FullPath,
                        sizeBytes,
                        response.PageCount,
                        response.StartPage,
                        response.EndPage,
                        response.TotalTextLength,
                        response.OutputLimitReached,
                        response.HasMoreAfter,
                        response.NextPage,
                        pages));
            }
            case "password_protected":
                return LoomResult<PdfTextReadResult>.Failure(
                    Error(
                        "unsupported",
                        "The PDF is password protected and passwords are not supported by this tool.",
                        retryable: false,
                        "password_protected_pdf"));
            case "invalid_pdf":
                return LoomResult<PdfTextReadResult>.Failure(
                    Error(
                        "unsupported",
                        "The file is not a valid supported PDF.",
                        retryable: false,
                        "invalid_pdf"));
            case "resource_limit":
                return LoomResult<PdfTextReadResult>.Failure(
                    Error(
                        "unsupported",
                        "PDF text extraction exceeded the worker memory limit.",
                        retryable: false,
                        "pdf_resource_limit"));
            case "page_out_of_range":
                return LoomResult<PdfTextReadResult>.Failure(
                    Error(
                        "invalid_argument",
                        $"startPage {request.StartPage} is outside the PDF page range 1..{response.PageCount}.",
                        retryable: false,
                        "page_out_of_range"));
            default:
                return Crashed(
                    $"PDF worker returned unknown status '{response.Status}'.",
                    null);
        }
    }

    private static LoomResult<PdfTextReadResult> Crashed(
        string message,
        string? diagnostics)
    {
        var suffix = BoundDiagnostics(diagnostics);
        return LoomResult<PdfTextReadResult>.Failure(
            Error(
                "execution_failed",
                string.IsNullOrWhiteSpace(suffix)
                    ? message
                    : $"{message} Diagnostics: {suffix}",
                retryable: false,
                "pdf_worker_crashed"));
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
