using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoomLCI.Launcher;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: init|apply|recover|verify <root> [pause-at-stage] [expected-active]");
    return 64;
}

var root = Path.GetFullPath(args[1]);
var paths = new AppPaths(Path.Combine(root, "install"), Path.Combine(root, "data"));
if (!root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ||
    root.IndexOf("LoomLCI.RB04.Crash.", StringComparison.OrdinalIgnoreCase) < 0)
{
    Console.Error.WriteLine("Refusing to touch a path outside RB-04 isolated TEMP.");
    return 65;
}

try
{
    switch (args[0])
    {
        case "init":
        {
            Directory.CreateDirectory(paths.VersionDirectory("v1"));
            Directory.CreateDirectory(paths.VersionDirectory("v2"));
            File.WriteAllText(paths.HostPath("v1"), "host-v1");
            File.WriteAllText(paths.HostPath("v2"), "old-v2-rollback");
            MachineConfigStore.Save(paths.MachineConfigPath, new MachineConfig
            {
                ActiveVersion = "v1", ActiveSequence = 1,
                PreviousVersion = "v2", PreviousSequence = 2,
                HighestSequence = 2,
                TunnelId = "tunnel_rb04_isolated",
                Alias = "loomlci-isolated",
                ProfileName = "loomlci-isolated",
                TunnelClientVersion = TunnelClient.PinnedVersion,
                TunnelClientArchiveSha256 = TunnelClient.PinnedArchiveSha256
            });
            File.WriteAllText(Path.Combine(root, "runtime.ready"), "v1");
            break;
        }
        case "apply":
        {
            var pauseAt = args[2];
            var package = BuildPackage();
            var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                channel = "stable",
                sequence = 2,
                version = "v2",
                platform = "win-x64",
                minUpdateProtocol = 2,
                packageUrl = "https://rb04.example.test/update.zip",
                packageSizeBytes = package.Length,
                packageSha256 = hash
            });

            using var key = CngKey.Create(CngAlgorithm.ECDsaP256);
            using var signer = new ECDsaCng(key);
            var signature = Convert.ToBase64String(
                signer.SignData(manifestBytes, HashAlgorithmName.SHA256));
            var handler = new BytesHandler(new Dictionary<string, byte[]>
            {
                ["https://rb04.example.test/manifest.json"] = manifestBytes,
                ["https://rb04.example.test/manifest.sig"] = Encoding.UTF8.GetBytes(signature),
                ["https://rb04.example.test/update.zip"] = package
            });
            var verifier = new CngUpdateSignatureVerifier(
                key.Export(CngKeyBlobFormat.EccPublicBlob));

            using var http = new HttpClient(handler);
            var service = new UpdateService(
                paths, new SimulatedRuntime(root), http, verifier,
                new UpdateFeedOptions(
                    new Uri("https://rb04.example.test/manifest.json"),
                    new Uri("https://rb04.example.test/manifest.sig")),
                transitionProbe: stage => PauseAt(stage, pauseAt, root));
            await service.ApplyAsync(CancellationToken.None);
            break;
        }
        case "recover":
        {
            var pauseAt = args.Length > 2 ? args[2] : string.Empty;
            var service = new UpdateService(
                paths, new SimulatedRuntime(root),
                transitionProbe: stage => PauseAt(stage, pauseAt, root));
            if (!await service.RecoverIfNeededAsync(CancellationToken.None))
            {
                throw new Exception("Expected a pending journal during recovery.");
            }
            break;
        }
        case "verify":
        {
            var expected = args[2];
            var config = MachineConfigStore.Load(paths.MachineConfigPath);
            if (config.ActiveVersion != expected)
                throw new Exception($"Expected active {expected}; got {config.ActiveVersion}");

            if (expected == "v2")
            {
                if (config.PreviousVersion != "v1" ||
                    File.ReadAllText(paths.HostPath("v2")) != "new-v2" ||
                    File.ReadAllText(Path.Combine(root, "runtime.ready")) != "v2")
                    throw new Exception("Committed target or its rollback is not intact.");
            }
            else if (config.PreviousVersion != "v2" ||
                     File.ReadAllText(paths.HostPath("v2")) != "old-v2-rollback" ||
                     File.ReadAllText(Path.Combine(root, "runtime.ready")) != "v1")
                throw new Exception("Original config/runtime or previous target was not restored.");

            if (UpdateJournalStore.TryLoad(paths.UpdateJournalPath) != null ||
                Directory.EnumerateDirectories(paths.VersionsRoot, "*.backup-*").Any() ||
                Directory.EnumerateDirectories(paths.VersionsRoot, "*.staging-*").Any())
                throw new Exception("Recovery artifacts or journal not cleaned.");

            Console.WriteLine("RB04_CRASH_RECOVERY_VERIFIED_" + expected);
            break;
        }
        default:
            throw new ArgumentException("Unknown command");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static void PauseAt(string current, string selected, string root)
{
    if (current != selected) return;
    // A separate external parent observes this marker then kills this OS process.
    File.WriteAllText(Path.Combine(root, "probe.ready"), current);
    Console.WriteLine("PROBE_READY_" + current);
    Console.Out.Flush();
    Thread.Sleep(TimeSpan.FromMinutes(3));
    throw new InvalidOperationException("A crash-test probe must have been terminated externally.");
}

static byte[] BuildPackage()
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
    {
        AddEntry(archive, "package.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            version = "v2",
            sequence = 2,
            updateProtocol = 2,
            platform = "win-x64",
            hostRelativePath = "payload/host"
        }));
        AddEntry(archive, "payload/host/LoomLCI.Host.exe", Encoding.UTF8.GetBytes("new-v2"));
    }
    return memory.ToArray();
}

static void AddEntry(ZipArchive archive, string path, byte[] bytes)
{
    using var entry = archive.CreateEntry(path, CompressionLevel.NoCompression).Open();
    entry.Write(bytes);
}

sealed class SimulatedRuntime(string root) : IUpdateRuntimeControl
{
    private string Marker => Path.Combine(root, "runtime.ready");

    public Task StopAndConfirmAsync(AppPaths paths, MachineConfig config, CancellationToken cancellationToken)
    {
        File.WriteAllText(Marker, "stopped");
        return Task.CompletedTask;
    }

    public Task StartAndConfirmAsync(AppPaths paths, MachineConfig config, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.HostPath(config.ActiveVersion)))
            throw new IOException("Missing expected Host during startup");
        File.WriteAllText(Marker, config.ActiveVersion);
        return Task.CompletedTask;
    }

    public Task<bool> IsReadyAsync(AppPaths paths, MachineConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(Marker) &&
                        File.ReadAllText(Marker) == config.ActiveVersion);
}

sealed class BytesHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null ||
            !responses.TryGetValue(request.RequestUri.AbsoluteUri, out var bytes))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
    }
}
