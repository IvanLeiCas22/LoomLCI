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

            LoomLCI.Core.Observability.DiagnosticsLog.SetEnabled(paths.DataRoot, true);
            var runtime = new FakeUpdateRuntimeControl("v1");
            var fixture = CreateFeed(
                version: "v2",
                sequence: 2);
            var service = fixture.CreateService(
                paths,
                runtime);

            var downloadProgress = new List<UpdateDownloadProgress>();
            var result = await service.ApplyAsync(
                CancellationToken.None, downloadProgress.Add);

            var config = MachineConfigStore.Load(
                paths.MachineConfigPath);

            Assert.NotEmpty(downloadProgress);
            Assert.Equal(downloadProgress[^1].TotalBytes,
                downloadProgress[^1].BytesReceived);
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
            var diagnosticLines = LoomLCI.Core.Observability.DiagnosticsLog.ReadTail(paths.DataRoot, 100);
            Assert.Contains(diagnosticLines, line => line.Contains("\"kind\":\"UpdateStage\"", StringComparison.Ordinal));
            Assert.Contains(diagnosticLines, line => line.Contains("\"outcome\":\"Committed\"", StringComparison.Ordinal));
            Assert.DoesNotContain("manifest.sig", string.Join("\n", diagnosticLines), StringComparison.Ordinal);
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
                    SchemaVersion = 1,
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
                    SchemaVersion = 1,
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

    [Theory]
    [InlineData("prepared")]
    [InlineData("promoting")]
    [InlineData("backed_up")]
    [InlineData("published")]
    public async Task InterruptedReapplyRestoresPreviouslyInstalledTarget(string stage)
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(
                root, "v1", 1, "v2", 2, 2);
            File.WriteAllText(paths.HostPath("v2"), "older-v2-bytes");
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            var runtime = new FakeUpdateRuntimeControl("v1");
            var fixture = CreateFeed("v2", 2);
            var service = fixture.CreateService(
                paths, runtime, probe: reached =>
                {
                    if (reached == stage)
                    {
                        throw new IOException("Injected interruption at " + stage);
                    }
                });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(CancellationToken.None));

            Assert.Equal(original, MachineConfigStore.Load(paths.MachineConfigPath));
            Assert.Equal("older-v2-bytes", File.ReadAllText(paths.HostPath("v2")));
            Assert.Equal("v1", runtime.ReadyVersion);
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.Empty(Directory.EnumerateDirectories(paths.VersionsRoot, "*.staging-*"));
            Assert.Empty(Directory.EnumerateDirectories(paths.VersionsRoot, "*.backup-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("promoting")]
    [InlineData("published")]
    public async Task InterruptedFreshTargetDoesNotDamageCurrentVersion(string stage)
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v0", 0, 1);
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            var fixture = CreateFeed("v2", 2);
            var runtime = new FakeUpdateRuntimeControl("v1");
            var service = fixture.CreateService(paths, runtime, probe: reached =>
            {
                if (reached == stage)
                {
                    throw new IOException("interrupted");
                }
            });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(CancellationToken.None));

            Assert.Equal(original, MachineConfigStore.Load(paths.MachineConfigPath));
            Assert.Equal("v1", File.ReadAllText(paths.HostPath("v1")));
            Assert.False(Directory.Exists(paths.VersionDirectory("v2")));
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.Empty(Directory.EnumerateDirectories(paths.VersionsRoot, "*.staging-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRecoversPromotionInterruptedBeforeOrAfterPublishing(bool published)
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v2", 2, 2);
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            File.WriteAllText(paths.HostPath("v2"), "old-copy");
            var id = Guid.NewGuid().ToString("N");
            var staging = paths.VersionDirectory("v2") + ".staging-" + id;
            var backup = paths.VersionDirectory("v2") + ".backup-" + id;
            CreateVersion(paths, "v2.staging-" + id);
            File.WriteAllText(Path.Combine(staging, "LoomLCI.Host.exe"), "new-copy");
            Directory.Move(paths.VersionDirectory("v2"), backup);
            if (published)
            {
                Directory.Move(staging, paths.VersionDirectory("v2"));
            }

            var target = original with
            {
                ActiveVersion = "v2", ActiveSequence = 2,
                PreviousVersion = "v1", PreviousSequence = 1
            };
            UpdateJournalStore.Save(paths.UpdateJournalPath, new UpdateJournal
            {
                Operation = "update", OperationId = id,
                Stage = UpdateJournalStage.Promoting,
                TargetExisted = true, OriginalConfig = original,
                TargetConfig = target, StartedAt = DateTimeOffset.UtcNow
            });
            var runtime = new FakeUpdateRuntimeControl("v1");
            var service = CreateFeed("v3", 3).CreateService(paths, runtime);

            Assert.True(await service.RecoverIfNeededAsync(CancellationToken.None));
            Assert.Equal(original, MachineConfigStore.Load(paths.MachineConfigPath));
            Assert.Equal("old-copy", File.ReadAllText(paths.HostPath("v2")));
            Assert.False(Directory.Exists(backup));
            Assert.False(Directory.Exists(staging));
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.False(await service.RecoverIfNeededAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestartFinalizesCommittedUpdateAndPreservesRollback()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v2", 2, 2);
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            var id = Guid.NewGuid().ToString("N");
            var destination = paths.VersionDirectory("v2");
            var backup = destination + ".backup-" + id;
            Directory.Move(destination, backup);
            CreateVersion(paths, "v2");
            File.WriteAllText(paths.HostPath("v2"), "new-release");
            var target = original with
            {
                ActiveVersion = "v2", ActiveSequence = 2,
                PreviousVersion = "v1", PreviousSequence = 1
            };
            MachineConfigStore.Save(paths.MachineConfigPath, target);
            UpdateJournalStore.Save(paths.UpdateJournalPath, new UpdateJournal
            {
                Operation = "update", OperationId = id, TargetExisted = true,
                Stage = UpdateJournalStage.Committed,
                OriginalConfig = original, TargetConfig = target,
                StartedAt = DateTimeOffset.UtcNow
            });
            var runtime = new FakeUpdateRuntimeControl("v2");
            var service = CreateFeed("v3", 3).CreateService(paths, runtime);

            Assert.True(await service.RecoverIfNeededAsync(CancellationToken.None));
            Assert.Equal("new-release", File.ReadAllText(paths.HostPath("v2")));
            Assert.True(File.Exists(paths.HostPath("v1")));
            Assert.False(Directory.Exists(backup));
            Assert.False(File.Exists(paths.UpdateJournalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("v1", 2)]
    [InlineData("V1", 2)]
    public async Task HigherSequenceCannotOverwriteActiveHost(string releaseVersion, long sequence)
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v0", 0, 1);
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            var fixture = CreateFeed(releaseVersion, sequence);
            var service = fixture.CreateService(paths, new FakeUpdateRuntimeControl("v1"));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.CheckAsync(CancellationToken.None));
            Assert.Equal(original, MachineConfigStore.Load(paths.MachineConfigPath));
            Assert.Equal("v1", File.ReadAllText(paths.HostPath("v1")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreviousVersionCannotBeReusedWithDifferentSequence()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v2", 2, 2);
            var service = CreateFeed("v2", 3).CreateService(
                paths, new FakeUpdateRuntimeControl("v1"));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.CheckAsync(CancellationToken.None));
            Assert.Equal("v2", File.ReadAllText(paths.HostPath("v2")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(UpdateJournalStage.Stopping, false)]
    [InlineData(UpdateJournalStage.RuntimeStopped, false)]
    [InlineData(UpdateJournalStage.Activated, true)]
    [InlineData(UpdateJournalStage.Starting, true)]
    [InlineData(UpdateJournalStage.RuntimeStarted, true)]
    public async Task InterruptedRuntimeActivationRestoresPreviousFilesAndConfig(
        UpdateJournalStage stage, bool configSwitched)
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v2", 2, 2);
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            File.WriteAllText(paths.HostPath("v2"), "saved-previous");
            var id = Guid.NewGuid().ToString("N");
            var backup = paths.VersionDirectory("v2") + ".backup-" + id;
            Directory.Move(paths.VersionDirectory("v2"), backup);
            CreateVersion(paths, "v2");
            File.WriteAllText(paths.HostPath("v2"), "published-target");
            var target = original with
            {
                ActiveVersion = "v2", ActiveSequence = 2,
                PreviousVersion = "v1", PreviousSequence = 1
            };
            if (configSwitched)
            {
                MachineConfigStore.Save(paths.MachineConfigPath, target);
            }

            UpdateJournalStore.Save(paths.UpdateJournalPath, new UpdateJournal
            {
                Operation = "update", OperationId = id,
                Stage = stage, TargetExisted = true,
                OriginalConfig = original, TargetConfig = target,
                StartedAt = DateTimeOffset.UtcNow
            });

            // A healthy target at RuntimeStarted is committed, not rolled back;
            // force a not-ready runtime to exercise the restoration branch.
            var runtime = new FakeUpdateRuntimeControl(null);
            var service = CreateFeed("v3", 3).CreateService(paths, runtime);
            Assert.True(await service.RecoverIfNeededAsync(CancellationToken.None));
            Assert.Equal(original, MachineConfigStore.Load(paths.MachineConfigPath));
            Assert.Equal("saved-previous", File.ReadAllText(paths.HostPath("v2")));
            Assert.Equal("v1", runtime.ReadyVersion);
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingPreviousTargetBackupPreservesJournalForManualRecovery()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v2", 2, 2);
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            var id = Guid.NewGuid().ToString("N");
            Directory.Delete(paths.VersionDirectory("v2"), recursive: true);
            var target = original with
            {
                ActiveVersion = "v2", ActiveSequence = 2,
                PreviousVersion = "v1", PreviousSequence = 1
            };
            UpdateJournalStore.Save(paths.UpdateJournalPath, new UpdateJournal
            {
                Operation = "update", OperationId = id,
                Stage = UpdateJournalStage.Promoting,
                TargetExisted = true,
                OriginalConfig = original, TargetConfig = target,
                StartedAt = DateTimeOffset.UtcNow
            });
            var service = CreateFeed("v3", 3).CreateService(
                paths, new FakeUpdateRuntimeControl("v1"));

            await Assert.ThrowsAsync<IOException>(
                () => service.RecoverIfNeededAsync(CancellationToken.None));
            Assert.True(File.Exists(paths.UpdateJournalPath));
            Assert.Equal(original, MachineConfigStore.Load(paths.MachineConfigPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExceptionAfterDurableCommitFinalizesInsteadOfClaimingRollback()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v2", 2, 2);
            var runtime = new FakeUpdateRuntimeControl("v1");
            var service = CreateFeed("v2", 2).CreateService(
                paths, runtime, probe: stage =>
                {
                    if (stage == "committed")
                    {
                        throw new IOException("Process lost after commit");
                    }
                });

            var result = await service.ApplyAsync(CancellationToken.None);
            Assert.True(result.Changed);
            Assert.Equal("v2", result.ActiveVersion);
            Assert.Equal("host-v2", File.ReadAllText(paths.HostPath("v2")));
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.Empty(Directory.EnumerateDirectories(paths.VersionsRoot, "*.backup-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryCanBeRepeatedAfterInterruptionDuringRestore()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, "v2", 2, 2);
            var original = MachineConfigStore.Load(paths.MachineConfigPath);
            File.WriteAllText(paths.HostPath("v2"), "saved-original-target");
            var id = Guid.NewGuid().ToString("N");
            var backup = paths.VersionDirectory("v2") + ".backup-" + id;
            Directory.Move(paths.VersionDirectory("v2"), backup);
            CreateVersion(paths, "v2");
            File.WriteAllText(paths.HostPath("v2"), "new-target");
            var target = original with
            {
                ActiveVersion = "v2", ActiveSequence = 2,
                PreviousVersion = "v1", PreviousSequence = 1
            };
            UpdateJournalStore.Save(paths.UpdateJournalPath, new UpdateJournal
            {
                Operation = "update", OperationId = id, Stage = UpdateJournalStage.Promoting,
                OriginalConfig = original, TargetConfig = target, TargetExisted = true,
                StartedAt = DateTimeOffset.UtcNow
            });

            var failOnce = true;
            var runtime = new FakeUpdateRuntimeControl("v1");
            var service = CreateFeed("v3", 3).CreateService(
                paths, runtime, probe: stage =>
                {
                    if (stage == "restored_files" && failOnce)
                    {
                        failOnce = false;
                        throw new IOException("Simulated crash during recovery");
                    }
                });
            await Assert.ThrowsAsync<IOException>(
                () => service.RecoverIfNeededAsync(CancellationToken.None));
            Assert.True(File.Exists(paths.UpdateJournalPath));
            Assert.True(Directory.Exists(backup));
            Assert.Equal("saved-original-target", File.ReadAllText(paths.HostPath("v2")));

            Assert.True(await service.RecoverIfNeededAsync(CancellationToken.None));
            Assert.Equal("saved-original-target", File.ReadAllText(paths.HostPath("v2")));
            Assert.False(File.Exists(paths.UpdateJournalPath));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Protocol2SignedReleaseIsAcceptedAndInstalled()
    {
        var root = CreateScratch();
        try
        {
            Assert.Equal(2, UpdateTrust.SupportedProtocol);
            var paths = CreateInstalledState(root, "v1", 1, null, 0, 1);
            var service = CreateFeed("v2", 2, minUpdateProtocol: 2, packageProtocol: 2)
                .CreateService(paths, new FakeUpdateRuntimeControl("v1"));

            var check = await service.CheckAsync(CancellationToken.None);
            Assert.True(check.UpdateAvailable);
            Assert.False(check.RequiresNewInstaller);
            var result = await service.ApplyAsync(CancellationToken.None);
            Assert.True(result.Changed);
            Assert.Equal("v2", result.ActiveVersion);
            Assert.Equal("host-v2", File.ReadAllText(paths.HostPath("v2")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Protocol2ReleaseRejectsProtocol1PackageBeforePublishing()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, null, 0, 1);
            var service = CreateFeed("v2", 2, minUpdateProtocol: 2, packageProtocol: 1)
                .CreateService(paths, new FakeUpdateRuntimeControl("v1"));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ApplyAsync(CancellationToken.None));
            Assert.Equal("v1", MachineConfigStore.Load(paths.MachineConfigPath).ActiveVersion);
            Assert.False(Directory.Exists(paths.VersionDirectory("v2")));
            Assert.False(File.Exists(paths.UpdateJournalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FutureProtocolReleaseRequiresNewInstaller()
    {
        var root = CreateScratch();
        try
        {
            var paths = CreateInstalledState(root, "v1", 1, null, 0, 1);
            var service = CreateFeed("v2", 2, minUpdateProtocol: 3, packageProtocol: 3)
                .CreateService(paths, new FakeUpdateRuntimeControl("v1"));
            Assert.True((await service.CheckAsync(CancellationToken.None)).RequiresNewInstaller);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(CancellationToken.None));
            Assert.Equal("v1", MachineConfigStore.Load(paths.MachineConfigPath).ActiveVersion);
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
        bool corruptSignature = false,
        int minUpdateProtocol = 1,
        int packageProtocol = 1)
    {
        var packageBytes = CreatePackage(
            version,
            sequence,
            packageProtocol);
        var packageHash = Convert.ToHexString(
                SHA256.HashData(packageBytes))
            .ToLowerInvariant();

        var manifest = new UpdateReleaseManifest
        {
            Sequence = sequence,
            Version = version,
            MinUpdateProtocol = minUpdateProtocol,
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
        long sequence,
        int packageProtocol = 1)
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
                    updateProtocol = packageProtocol,
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
            IUpdateRuntimeControl runtime,
            Action<string>? probe = null)
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
                        "https://updates.test/manifest.sig")),
                transitionProbe: probe);
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
