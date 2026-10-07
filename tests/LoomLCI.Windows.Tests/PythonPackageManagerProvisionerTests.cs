#pragma warning disable CA1416

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LoomLCI.Windows.Python;

namespace LoomLCI.Windows.Tests;

public sealed class PythonPackageManagerProvisionerTests
{
    [Fact]
    public void EmbeddedManifestPinsValidatedUvRelease()
    {
        var manifest = PythonPackageManagerAssets.Manifest;

        Assert.Equal("0.12.23", manifest.Version);
        Assert.Equal(
            "x86_64-pc-windows-msvc",
            manifest.Architecture);
        Assert.Equal("uv.exe", manifest.Executable);
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.Contains(
            "/0.12.23/",
            manifest.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionerInstallsVerifiedArchiveAndReusesIt()
    {
        var root = TemporaryDirectory();
        var archive = CreateArchive();
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(archive));
        var handler = new StaticArchiveHandler(archive);
        using var http = new HttpClient(handler);

        try
        {
            var provisioner = new PythonPackageManagerProvisioner(
                http,
                root,
                FakeManifest(hash));

            var first = await provisioner.EnsureAsync(
                CancellationToken.None);
            var second = await provisioner.EnsureAsync(
                CancellationToken.None);

            Assert.True(first.IsSuccess, first.Error?.Message);
            Assert.True(second.IsSuccess, second.Error?.Message);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(
                first.Value!.ExecutablePath,
                second.Value!.ExecutablePath);
            Assert.True(File.Exists(first.Value.ExecutablePath));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(first.Value.ExecutablePath)!,
                ".loom-uv.json")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ProvisionerRejectsBadHashAndCleansStaging()
    {
        var root = TemporaryDirectory();
        var handler = new StaticArchiveHandler(
            CreateArchive());
        using var http = new HttpClient(handler);

        try
        {
            var provisioner = new PythonPackageManagerProvisioner(
                http,
                root,
                FakeManifest(new string('0', 64)));

            var result = await provisioner.EnsureAsync(
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                "execution_failed",
                result.Error?.Code);

            var parent = Path.Combine(
                root,
                "tools",
                "python",
                "uv");
            if (Directory.Exists(parent))
            {
                Assert.Empty(
                    Directory.EnumerateDirectories(
                        parent,
                        ".staging-*"));
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private sealed class StaticArchiveHandler(
        byte[] archive) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount
            => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archive)
                });
        }
    }

    private static PythonPackageManagerManifest FakeManifest(
        string sha256)
        => new(
            "0.12.23",
            "x86_64-pc-windows-msvc",
            "https://packages.invalid/uv.zip",
            sha256,
            "uv.exe");

    private static byte[] CreateArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(
                   stream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var entry = archive.CreateEntry("uv.exe");
            using var writer = new StreamWriter(
                entry.Open(),
                new UTF8Encoding(false));
            writer.Write("fake uv");
        }

        return stream.ToArray();
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-uv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
