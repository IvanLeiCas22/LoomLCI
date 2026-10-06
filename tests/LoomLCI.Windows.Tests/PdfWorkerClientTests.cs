using LoomLCI.Core.VisualFiles;
using LoomLCI.Windows.VisualFiles;

namespace LoomLCI.Windows.Tests;

public sealed class PdfWorkerClientTests
{
    [Fact]
    public async Task ExplicitResourceLimitStatusMapsToPdfResourceLimit()
    {
        var client = CreateClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"status\":\"resource_limit\"}')");

        var result = await client.ReadAsync(Request(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("pdf_resource_limit", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task PasswordProtectedStatusMapsToSpecificReason()
    {
        var client = CreateClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"status\":\"password_protected\"}')");

        var result = await client.ReadAsync(Request(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("password_protected_pdf", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task NonZeroWorkerExitMapsToCrashAndKeepsBoundedDiagnostics()
    {
        var client = CreateClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Error.WriteLine('controlled boom'); exit 7");

        var result = await client.ReadAsync(Request(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("execution_failed", result.Error?.Code);
        Assert.Equal("pdf_worker_crashed", result.Error?.Details?["reason"]);
        Assert.Contains("controlled boom", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerDeadlineTerminatesChildAndMapsToSpecificReason()
    {
        var client = CreateClient(
            "$null = [Console]::In.ReadLine(); Start-Sleep -Seconds 5",
            TimeSpan.FromMilliseconds(250));

        var started = DateTime.UtcNow;
        var result = await client.ReadAsync(Request(), 123, CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.False(result.IsSuccess);
        Assert.Equal("deadline_exceeded", result.Error?.Code);
        Assert.Equal("pdf_worker_timeout", result.Error?.Details?["reason"]);
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"Worker cleanup took {elapsed}.");
    }

    private static PdfWorkerClient CreateClient(
        string script,
        TimeSpan? timeout = null)
        => new(
            new PdfWorkerLaunchDescriptor(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script + "; #"],
                Environment.CurrentDirectory),
            timeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public async Task RenderExplicitResourceLimitMapsToSpecificReason()
    {
        var client = CreateRenderClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"status\":\"resource_limit\"}')");

        var result = await client.RenderAsync(RenderRequest(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("pdf_render_resource_limit", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task RenderPasswordProtectedStatusMapsToSpecificReason()
    {
        var client = CreateRenderClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"status\":\"password_protected\"}')");

        var result = await client.RenderAsync(RenderRequest(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("password_protected_pdf", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task RenderUnsupportedSecurityMapsToSpecificReason()
    {
        var client = CreateRenderClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"status\":\"unsupported_security\"}')");

        var result = await client.RenderAsync(RenderRequest(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("unsupported_pdf_security", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task RenderedImageTooLargeMapsToSpecificReason()
    {
        var client = CreateRenderClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"status\":\"rendered_image_too_large\"}')");

        var result = await client.RenderAsync(RenderRequest(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported", result.Error?.Code);
        Assert.Equal("rendered_image_too_large", result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task RenderDeadlineTerminatesChildAndMapsToSpecificReason()
    {
        var client = CreateRenderClient(
            "$null = [Console]::In.ReadLine(); Start-Sleep -Seconds 5",
            TimeSpan.FromMilliseconds(250));

        var started = DateTime.UtcNow;
        var result = await client.RenderAsync(RenderRequest(), 123, CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.False(result.IsSuccess);
        Assert.Equal("deadline_exceeded", result.Error?.Code);
        Assert.Equal("pdf_render_worker_timeout", result.Error?.Details?["reason"]);
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"Worker cleanup took {elapsed}.");
    }

    [Fact]
    public async Task RenderNonZeroExitMapsToRenderWorkerCrash()
    {
        var client = CreateRenderClient(
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Error.WriteLine('render boom'); exit 9");

        var result = await client.RenderAsync(RenderRequest(), 123, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("execution_failed", result.Error?.Code);
        Assert.Equal("pdf_render_worker_crashed", result.Error?.Details?["reason"]);
        Assert.Contains("render boom", result.Error?.Message, StringComparison.Ordinal);
    }

    private static PdfRenderWorkerClient CreateRenderClient(
        string script,
        TimeSpan? timeout = null)
        => new(
            new PdfWorkerLaunchDescriptor(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script + "; #"],
                Environment.CurrentDirectory),
            timeout ?? TimeSpan.FromSeconds(5));

    private static PdfTextReadRequest Request()
        => new("sample.pdf", @"C:\fake\sample.pdf", 1, 1);

    private static PdfPageRenderRequest RenderRequest()
        => new("sample.pdf", @"C:\fake\sample.pdf", 1, 1200, 1200);
}
