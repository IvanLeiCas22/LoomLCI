using System.Security.AccessControl;
using System.Security.Principal;
using LoomLCI.Launcher;

namespace LoomLCI.Launcher.Tests;

public sealed class LauncherTests
{
    [Fact]
    public void TunnelStatusParsesReadyRuntime()
    {
        const string json = """
            {
              "process_running": true,
              "healthy": true,
              "ready": true,
              "runtime_state": "ready",
              "tunnel_id": "tunnel_123",
              "error": ""
            }
            """;

        var status = TunnelRuntimeStatus.Parse(json);

        Assert.True(status.IsReady);
        Assert.Equal("tunnel_123", status.TunnelId);
    }

    [Fact]
    public void TunnelStatusRequiresAllReadySignals()
    {
        const string json = """
            {
              "process_running": true,
              "healthy": true,
              "ready": false,
              "runtime_state": "ready",
              "tunnel_id": "tunnel_123"
            }
            """;

        var status = TunnelRuntimeStatus.Parse(json);

        Assert.False(status.IsReady);
    }

    [Fact]
    public void MachineConfigRoundTripsWithoutSecret()
    {
        var root = CreateScratch();
        try
        {
            var path = Path.Combine(root, "machine.json");
            var expected = new MachineConfig
            {
                ActiveVersion = "0.1.0-test",
                PreviousVersion = "0.0.9",
                TunnelId = "tunnel_test",
                Alias = "loomlci-installed",
                ProfileName = "loomlci-installed",
                TunnelClientVersion = TunnelClient.PinnedVersion,
                TunnelClientArchiveSha256 = TunnelClient.PinnedArchiveSha256
            };

            MachineConfigStore.Save(path, expected);
            var actual = MachineConfigStore.Load(path);
            var json = File.ReadAllText(path);

            Assert.Equal(expected, actual);
            Assert.DoesNotContain("runtime-api-key", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sk-", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetupOptionsNeverAcceptLiteralRuntimeKeyArgument()
    {
        Assert.Throws<ArgumentException>(() =>
            SetupOptions.Parse(["--runtime-key", "secret"]));

        var parsed = SetupOptions.Parse(
            [
                "--tunnel-id", "tunnel_x",
                "--runtime-key-file", @"C:\temp\key.txt",
                "--alias", "pc-1",
                "--no-shortcut"
            ]);

        Assert.Equal("tunnel_x", parsed.TunnelId);
        Assert.Equal(@"C:\temp\key.txt", parsed.RuntimeKeyFile);
        Assert.Equal("pc-1", parsed.Alias);
        Assert.True(parsed.NoShortcut);
    }

    [Fact]
    public void TunnelEnvironmentUsesIsolatedStateAndActiveVersionOnPath()
    {
        var root = CreateScratch();
        try
        {
            var paths = new AppPaths(
                Path.Combine(root, "install"),
                Path.Combine(root, "data"));
            var config = new MachineConfig
            {
                ActiveVersion = "v1",
                TunnelId = "tunnel_x",
                Alias = "loomlci-installed",
                ProfileName = "loomlci-installed",
                TunnelClientVersion = TunnelClient.PinnedVersion,
                TunnelClientArchiveSha256 = TunnelClient.PinnedArchiveSha256
            };

            var client = new TunnelClient(new ProcessRunner());
            var environment = client.BuildEnvironment(paths, config);

            Assert.Equal(paths.ProfilesRoot, environment["TUNNEL_CLIENT_PROFILE_DIR"]);
            Assert.Equal(paths.TunnelStateRoot, environment["TUNNEL_CLIENT_STATE_DIR"]);
            Assert.StartsWith(
                paths.VersionDirectory("v1") + Path.PathSeparator,
                environment["PATH"],
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SecretFileUsesProtectedCurrentUserAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateScratch();
        try
        {
            var path = Path.Combine(root, "runtime-api-key.txt");
            SecretFile.WriteUserOnly(path, "test-secret");
            SecretFile.VerifyUserOnly(path);

            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!;
            var security = new FileInfo(path).GetAccessControl();
            var rules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();

            Assert.True(security.AreAccessRulesProtected);
            Assert.DoesNotContain(rules, rule => rule.IsInherited);
            Assert.DoesNotContain(
                rules,
                rule =>
                    rule.AccessControlType == AccessControlType.Allow &&
                    !Equals(rule.IdentityReference, user));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateScratch()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.Launcher.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
