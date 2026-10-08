using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace LoomLCI.Launcher;

public sealed record UpdateDownloadOptions
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxAttempts { get; init; } = 4;

    public void Validate()
    {
        if (IdleTimeout <= TimeSpan.Zero ||
            TotalTimeout <= TimeSpan.Zero ||
            ProgressInterval <= TimeSpan.Zero ||
            RetryDelay < TimeSpan.Zero ||
            MaxAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(UpdateDownloadOptions),
                "Opciones de descarga inválidas.");
        }
    }
}

public sealed record UpdateDownloadProgress(
    long BytesReceived,
    long TotalBytes,
    int Attempt,
    double BytesPerSecond);

public sealed class UpdatePackageDownloader
{
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly UpdateDownloadOptions _options;

    public UpdatePackageDownloader(
        HttpClient http,
        UpdateDownloadOptions? options = null)
    {
        _http = http;
        _options = options ?? new UpdateDownloadOptions();
        _options.Validate();
    }

    public async Task DownloadAsync(
        UpdateReleaseManifest release,
        string destination,
        Action<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (release.PackageSizeBytes is <= 0 or > MaxPackageBytes)
        {
            throw new InvalidDataException(
                $"Tamaño de paquete inválido: {release.PackageSizeBytes}.");
        }

        var uri = new Uri(release.PackageUrl, UriKind.Absolute);
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.TotalTimeout);

        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            useAsync: true);

        var timer = Stopwatch.StartNew();
        var lastReportTime = TimeSpan.Zero;
        long lastReportBytes = 0;

        void Report(int attempt, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            var elapsed = timer.Elapsed - lastReportTime;
            if (!force && elapsed < _options.ProgressInterval)
            {
                return;
            }

            var bytes = target.Length;
            var speed = elapsed.TotalSeconds > 0
                ? Math.Max(0, bytes - lastReportBytes) / elapsed.TotalSeconds
                : 0;
            progress(new UpdateDownloadProgress(
                bytes, release.PackageSizeBytes, attempt, speed));
            lastReportBytes = bytes;
            lastReportTime = timer.Elapsed;
        }

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDeadlineExpired(deadline, cancellationToken);

            try
            {
                var offset = target.Length;
                Report(attempt, force: true);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                }

                // HttpClient.Timeout stops covering the body with ResponseHeadersRead.
                // Bound the header phase and every subsequent network read independently.
                using var headerTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(deadline.Token);
                headerTimeout.CancelAfter(_options.IdleTimeout);

                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    headerTimeout.Token);

                if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    // The server ignored Range. Restart cleanly; never append 200.
                    target.SetLength(0);
                    target.Position = 0;
                    offset = 0;
                    Report(attempt, force: true);
                }
                else if (offset > 0)
                {
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent ||
                        range?.From != offset ||
                        range.To != release.PackageSizeBytes - 1 ||
                        range.Length != release.PackageSizeBytes)
                    {
                        throw new InvalidDataException(
                            "La respuesta HTTP Range no coincide con el paquete firmado.");
                    }
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                {
                    response.EnsureSuccessStatusCode();
                    throw new InvalidDataException(
                        "El servidor devolvió una respuesta parcial inesperada.");
                }

                var remaining = release.PackageSizeBytes - offset;
                if (response.Content.Headers.ContentLength is long declared &&
                    declared != remaining)
                {
                    throw new InvalidDataException(
                        $"Longitud HTTP inesperada: {declared}; esperada {remaining}.");
                }

                using var openTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(deadline.Token);
                openTimeout.CancelAfter(_options.IdleTimeout);
                await using var source = await response.Content
                    .ReadAsStreamAsync(openTimeout.Token);

                target.Position = offset;
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    using var idle = CancellationTokenSource
                        .CreateLinkedTokenSource(deadline.Token);
                    idle.CancelAfter(_options.IdleTimeout);

                    int read;
                    try
                    {
                        read = await source.ReadAsync(
                            buffer.AsMemory(), idle.Token);
                    }
                    catch (OperationCanceledException)
                        when (!deadline.IsCancellationRequested &&
                              idle.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "La descarga no recibió datos durante el tiempo permitido.");
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    if (target.Position + read > release.PackageSizeBytes)
                    {
                        throw new InvalidDataException(
                            "El servidor envió más bytes de los declarados.");
                    }

                    await target.WriteAsync(
                        buffer.AsMemory(0, read), deadline.Token);
                    Report(attempt);
                }

                if (target.Length != release.PackageSizeBytes)
                {
                    throw new IOException(
                        $"Descarga interrumpida: {target.Length} de " +
                        $"{release.PackageSizeBytes} bytes.");
                }

                await target.FlushAsync(deadline.Token);
                target.Position = 0;
                var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(target, deadline.Token))
                    .ToLowerInvariant();

                if (!string.Equals(
                    actualHash,
                    release.PackageSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SHA-256 inesperado para el update: {actualHash}.");
                }

                Report(attempt, force: true);
                return;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Se superó el tiempo máximo total de descarga.");
            }
            catch (Exception ex) when (
                attempt < _options.MaxAttempts &&
                !deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested &&
                IsTransient(ex))
            {
                // Preserve verified byte offsets for a Range resume on next attempt.
                await Task.Delay(
                    TimeSpan.FromTicks(
                        _options.RetryDelay.Ticks * attempt),
                    deadline.Token);
            }
        }
    }

    private static bool IsTransient(Exception error) =>
        error is TimeoutException or IOException or OperationCanceledException ||
        error is HttpRequestException http &&
        (http.StatusCode is null or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout);

    private static void ThrowIfDeadlineExpired(
        CancellationTokenSource deadline,
        CancellationToken cancellationToken)
    {
        if (deadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Se superó el tiempo máximo total de descarga.");
        }
    }
}
