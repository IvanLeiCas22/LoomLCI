using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoomLCI.Launcher;

namespace LoomLCI.Launcher.Tests;

public sealed class UpdateTests
{
    [Fact]
    public void LegacyMachineConfigLoadsWithZeroSequences()
    {
        var root = CreateScratch();
        try
        {
            var path = Path.Combine(root, "machine.json");
            File.WriteAllText(
                path,
                """
                {
                  "schemaVersion": 1,
                  "activeVersion": "legacy",
                  "previousVersion": "older",
                  "tunnelId": "tunnel_test",
                  "alias": "loomlci-installed",
                  "profileName": "loomlci-installed",
                  "tunnelClientVersion": "0.0.14",
                  "tunnelClientArchiveSha256": "784ab8da7b5a88f0109f1fd8aaf0a1c86067430b896dddf307ef7e3cc49fa1a5"
                }
                """);

            var config = MachineConfigStore.Load(path);

            Assert.Equal(0, config.ActiveSequence);
            Assert.Equal(0, config.PreviousSequence);
            Assert.Equal(0, config.HighestSequence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SignatureVerifierAcceptsExpectedKeyAndRejectsMutation()
    {
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256);
        using var signer = new ECDsaCng(key);

        var data = Encoding.UTF8.GetBytes("signed manifest");
        var signature = signer.SignData(
            data,
            HashAlgorithmName.SHA256);
        var publicBlob = key.Export(
            CngKeyBlobFormat.EccPublicBlob);
        var verifier = new CngUpdateSignatureVerifier(
            publicBlob);

        Assert.True(verifier.Verify(data, signature));

        data[0] ^= 0x01;
        Assert.False(verifier.Verify(data, signature));
    }

    [Fact]
    public async Task ApplyUpdateActivatesNewVersionAndCleansOldVersions()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v1",
                activeSequence: 1,
                previousVersion: "v0",
                previousSequence: 0,
                highestSequence: 1,
                extraVersions: ["stale"]);

            var runtime = new FakeUpdateRuntimeControl("v1");
            var fixture = CreateFeed(
                version: "v2",
                sequence: 2);
            var service = fixture.CreateService(
                paths,
                runtime);

            var result = await service.ApplyAsync(
                CancellationToken.None);

            var config = MachineConfigStore.Load(
                paths.MachineConfigPath);

            Assert.True(result.Changed);
            Assert.Equal("v2", config.ActiveVersion);
            Assert.Equal(2, config.ActiveSequence);
            Assert.Equal("v1", config.PreviousVersion);
            Assert.Equal(1, config.PreviousSequence);
            Assert.Equal(2, config.HighestSequence);
            Assert.True(Directory.Exists(
                paths.VersionDirectory("v2")));
            Assert.True(Directory.Exists(
                paths.VersionDirectory("v1")));
            Assert.False(Directory.Exists(
                paths.VersionDirectory("v0")));
            Assert.False(Directory.Exists(
                paths.VersionDirectory("stale")));
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.Equal(["v1"], runtime.Stops);
            Assert.Equal(["v2"], runtime.Starts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReapplyAfterRollbackReinstallsExistingTargetFromSignedPackage()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v1",
                activeSequence: 1,
                previousVersion: "v2",
                previousSequence: 2,
                highestSequence: 2);
            File.WriteAllText(
                paths.HostPath("v2"),
                "corrupted-old-copy");

            var runtime = new FakeUpdateRuntimeControl("v1");
            var fixture = CreateFeed(
                version: "v2",
                sequence: 2);
            var service = fixture.CreateService(
                paths,
                runtime);

            await service.ApplyAsync(
                CancellationToken.None);

            Assert.Equal(
                "host-v2",
                File.ReadAllText(paths.HostPath("v2")));
            var config = MachineConfigStore.Load(
                paths.MachineConfigPath);
            Assert.Equal("v2", config.ActiveVersion);
            Assert.Equal(2, config.HighestSequence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedUpdateRestoresOriginalVersion()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v1",
                activeSequence: 1,
                previousVersion: "v0",
                previousSequence: 0,
                highestSequence: 1);

            var runtime = new FakeUpdateRuntimeControl("v1")
            {
                FailStartVersion = "v2"
            };
            var fixture = CreateFeed(
                version: "v2",
                sequence: 2);
            var service = fixture.CreateService(
                paths,
                runtime);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    CancellationToken.None));

            var config = MachineConfigStore.Load(
                paths.MachineConfigPath);

            Assert.Contains(
                "restauró v1",
                error.Message,
                StringComparison.Ordinal);
            Assert.Equal("v1", config.ActiveVersion);
            Assert.Equal(1, config.ActiveSequence);
            Assert.Equal("v0", config.PreviousVersion);
            Assert.Equal(1, config.HighestSequence);
            Assert.Equal("v1", runtime.ReadyVersion);
            Assert.False(Directory.Exists(
                paths.VersionDirectory("v2")));
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.Equal(["v2", "v1"], runtime.Starts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedRuntimeStartIsFinalizedAfterCrashBeforeJournalDelete()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v1",
                activeSequence: 1,
                previousVersion: "v0",
                previousSequence: 0,
                highestSequence: 1,
                extraVersions: ["stale"]);
            var original = MachineConfigStore.Load(
                paths.MachineConfigPath);

            CreateVersion(paths, "v2");
            var target = original with
            {
                ActiveVersion = "v2",
                ActiveSequence = 2,
                PreviousVersion = "v1",
                PreviousSequence = 1,
                HighestSequence = 2
            };
            MachineConfigStore.Save(
                paths.MachineConfigPath,
                target);
            UpdateJournalStore.Save(
                paths.UpdateJournalPath,
                new UpdateJournal
                {
                    Operation = "update",
                    OperationId = "test-finalize",
                    Stage = UpdateJournalStage.RuntimeStarted,
                    OriginalConfig = original,
                    TargetConfig = target,
                    StartedAt = DateTimeOffset.UtcNow
                });

            var runtime = new FakeUpdateRuntimeControl("v2");
            var fixture = CreateFeed(
                version: "v3",
                sequence: 3);
            var service = fixture.CreateService(
                paths,
                runtime);

            Assert.True(await service.RecoverIfNeededAsync(
                CancellationToken.None));

            var recovered = MachineConfigStore.Load(
                paths.MachineConfigPath);
            Assert.Equal("v2", recovered.ActiveVersion);
            Assert.Equal("v1", recovered.PreviousVersion);
            Assert.Equal("v2", runtime.ReadyVersion);
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.True(Directory.Exists(
                paths.VersionDirectory("v2")));
            Assert.True(Directory.Exists(
                paths.VersionDirectory("v1")));
            Assert.False(Directory.Exists(
                paths.VersionDirectory("v0")));
            Assert.False(Directory.Exists(
                paths.VersionDirectory("stale")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IncompleteActivationIsRecoveredOnNextLauncherOperation()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v1",
                activeSequence: 1,
                previousVersion: "v0",
                previousSequence: 0,
                highestSequence: 1);
            var original = MachineConfigStore.Load(
                paths.MachineConfigPath);

            CreateVersion(paths, "v2");
            var target = original with
            {
                ActiveVersion = "v2",
                ActiveSequence = 2,
                PreviousVersion = "v1",
                PreviousSequence = 1,
                HighestSequence = 2
            };
            MachineConfigStore.Save(
                paths.MachineConfigPath,
                target);
            UpdateJournalStore.Save(
                paths.UpdateJournalPath,
                new UpdateJournal
                {
                    Operation = "update",
                    OperationId = "test",
                    Stage = UpdateJournalStage.Starting,
                    OriginalConfig = original,
                    TargetConfig = target,
                    StartedAt = DateTimeOffset.UtcNow
                });

            var runtime = new FakeUpdateRuntimeControl("v2");
            var fixture = CreateFeed(
                version: "v3",
                sequence: 3);
            var service = fixture.CreateService(
                paths,
                runtime);

            Assert.True(await service.RecoverIfNeededAsync(
                CancellationToken.None));

            var recovered = MachineConfigStore.Load(
                paths.MachineConfigPath);
            Assert.Equal("v1", recovered.ActiveVersion);
            Assert.Equal(1, recovered.ActiveSequence);
            Assert.Equal("v1", runtime.ReadyVersion);
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.False(Directory.Exists(
                paths.VersionDirectory("v2")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackSwapsVersionsAndPreservesHighWatermark()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v2",
                activeSequence: 2,
                previousVersion: "v1",
                previousSequence: 1,
                highestSequence: 2);

            var runtime = new FakeUpdateRuntimeControl("v2");
            var fixture = CreateFeed(
                version: "v3",
                sequence: 3);
            var service = fixture.CreateService(
                paths,
                runtime);

            var result = await service.RollbackAsync(
                CancellationToken.None);

            var config = MachineConfigStore.Load(
                paths.MachineConfigPath);

            Assert.True(result.Changed);
            Assert.Equal("v1", config.ActiveVersion);
            Assert.Equal(1, config.ActiveSequence);
            Assert.Equal("v2", config.PreviousVersion);
            Assert.Equal(2, config.PreviousSequence);
            Assert.Equal(2, config.HighestSequence);
            Assert.Equal("v1", runtime.ReadyVersion);
            Assert.False(File.Exists(paths.UpdateJournalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SignedFeedCannotRollbackBelowHighWatermark()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v1",
                activeSequence: 1,
                previousVersion: "v2",
                previousSequence: 2,
                highestSequence: 2);

            var runtime = new FakeUpdateRuntimeControl("v1");
            var fixture = CreateFeed(
                version: "v1",
                sequence: 1);
            var service = fixture.CreateService(
                paths,
                runtime);

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.CheckAsync(
                    CancellationToken.None));

            Assert.Contains(
                "feed retrocedió",
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidManifestSignatureIsRejectedBeforePackageDownload()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root,
                activeVersion: "v1",
                activeSequence: 1,
                previousVersion: null,
                previousSequence: 0,
                highestSequence: 1);

            var runtime = new FakeUpdateRuntimeControl("v1");
            var fixture = CreateFeed(
                version: "v2",
                sequence: 2,
                corruptSignature: true);
            var service = fixture.CreateService(
                paths,
                runtime);

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.CheckAsync(
                    CancellationToken.None));

            Assert.Contains(
                "firma",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, fixture.Handler.RequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeploymentLockRejectsConcurrentMutation()
    {
        var root = CreateScratch();
        try
        {
            var paths = new AppPaths(
                Path.Combine(root, "install"),
                Path.Combine(root, "data"));

            using var first = DeploymentLock.Acquire(paths);

            Assert.Throws<InvalidOperationException>(
                () => DeploymentLock.Acquire(paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AppPaths CreateInstalledState(
        string root,
        string activeVersion,
        long activeSequence,
        string? previousVersion,
        long previousSequence,
        long highestSequence,
        string[]? extraVersions = null)
    {
        var paths = new AppPaths(
            Path.Combine(root, "install"),
            Path.Combine(root, "data"));

        var config = new MachineConfig
        {
            ActiveVersion = activeVersion,
            ActiveSequence = activeSequence,
            PreviousVersion = previousVersion,
            PreviousSequence = previousSequence,
            HighestSequence = highestSequence,
            TunnelId = "tunnel_test",
            Alias = "loomlci-installed",
            ProfileName = "loomlci-installed",
            TunnelClientVersion = TunnelClient.PinnedVersion,
            TunnelClientArchiveSha256 =
                TunnelClient.PinnedArchiveSha256
        };

        MachineConfigStore.Save(
            paths.MachineConfigPath,
            config);

        CreateVersion(paths, activeVersion);
        if (!string.IsNullOrWhiteSpace(previousVersion))
        {
            CreateVersion(paths, previousVersion);
        }

        foreach (var version in extraVersions ?? [])
        {
            CreateVersion(paths, version);
        }

        return paths;
    }

    private static void CreateVersion(
        AppPaths paths,
        string version)
    {
        var directory = paths.VersionDirectory(version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "LoomLCI.Host.exe"),
            version);
    }

    private static SignedFeedFixture CreateFeed(
        string version,
        long sequence,
        bool corruptSignature = false)
    {
        var packageBytes = CreatePackage(
            version,
            sequence);
        var packageHash = Convert.ToHexString(
                SHA256.HashData(packageBytes))
            .ToLowerInvariant();

        var manifest = new UpdateReleaseManifest
        {
            Sequence = sequence,
            Version = version,
            PackageUrl =
                "https://updates.test/package.zip",
            PackageSizeBytes = packageBytes.Length,
            PackageSha256 = packageHash
        };

        var manifestBytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy =
                        JsonNamingPolicy.CamelCase
                }));

        using var key = CngKey.Create(
            CngAlgorithm.ECDsaP256);
        using var signer = new ECDsaCng(key);

        var signature = signer.SignData(
            manifestBytes,
            HashAlgorithmName.SHA256);
        if (corruptSignature)
        {
            signature[0] ^= 0x01;
        }

        var publicBlob = key.Export(
            CngKeyBlobFormat.EccPublicBlob);
        var handler = new StaticHttpHandler(
            new Dictionary<string, byte[]>(
                StringComparer.Ordinal)
            {
                ["https://updates.test/manifest.json"] =
                    manifestBytes,
                ["https://updates.test/manifest.sig"] =
                    Encoding.UTF8.GetBytes(
                        Convert.ToBase64String(signature)),
                ["https://updates.test/package.zip"] =
                    packageBytes
            });

        return new SignedFeedFixture(
            handler,
            new CngUpdateSignatureVerifier(
                publicBlob));
    }

    private static byte[] CreatePackage(
        string version,
        long sequence)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(
                   memory,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var packageManifest = JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    version,
                    sequence,
                    updateProtocol = 1,
                    platform = "win-x64",
                    hostRelativePath = "payload/host"
                });

            AddEntry(
                archive,
                "package.json",
                Encoding.UTF8.GetBytes(packageManifest));
            AddEntry(
                archive,
                "payload/host/LoomLCI.Host.exe",
                Encoding.UTF8.GetBytes(
                    "host-" + version));
        }

        return memory.ToArray();
    }

    private static void AddEntry(
        ZipArchive archive,
        string name,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(
            name,
            CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string CreateScratch()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.Update.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record SignedFeedFixture(
        StaticHttpHandler Handler,
        IUpdateSignatureVerifier Verifier)
    {
        public UpdateService CreateService(
            AppPaths paths,
            IUpdateRuntimeControl runtime)
        {
            return new UpdateService(
                paths,
                runtime,
                new HttpClient(Handler),
                Verifier,
                new UpdateFeedOptions(
                    new Uri(
                        "https://updates.test/manifest.json"),
                    new Uri(
                        "https://updates.test/manifest.sig")));
        }
    }

    private sealed class StaticHttpHandler :
        HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, byte[]>
            _responses;

        public StaticHttpHandler(
            IReadOnlyDictionary<string, byte[]> responses)
        {
            _responses = responses;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            if (request.RequestUri is null ||
                !_responses.TryGetValue(
                    request.RequestUri.AbsoluteUri,
                    out var bytes))
            {
                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.NotFound));
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
        }
    }
}

internal sealed class FakeUpdateRuntimeControl :
    IUpdateRuntimeControl
{
    public FakeUpdateRuntimeControl(
        string? readyVersion = null)
    {
        ReadyVersion = readyVersion;
    }

    public string? FailStartVersion { get; init; }
    public string? ReadyVersion { get; private set; }
    public List<string> Starts { get; } = [];
    public List<string> Stops { get; } = [];

    public Task StopAndConfirmAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        Stops.Add(config.ActiveVersion);
        ReadyVersion = null;
        return Task.CompletedTask;
    }

    public Task StartAndConfirmAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken)
    {
        Starts.Add(config.ActiveVersion);

        if (string.Equals(
                config.ActiveVersion,
                FailStartVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "simulated start failure");
        }

        ReadyVersion = config.ActiveVersion;
        return Task.CompletedTask;
    }

    public Task<bool> IsReadyAsync(
        AppPaths paths,
        MachineConfig config,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            string.Equals(
                ReadyVersion,
                config.ActiveVersion,
                StringComparison.Ordinal));
}
