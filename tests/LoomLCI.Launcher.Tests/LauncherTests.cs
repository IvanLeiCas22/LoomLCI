using System.IO.Compression;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
                ActiveSequence = 7,
                PreviousVersion = "0.0.9",
                PreviousSequence = 6,
                HighestSequence = 7,
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


    [Fact]
    public async Task PauseOptionPromptsBeforeExit()
    {
        var root = CreateScratch();
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var input = new StringReader(Environment.NewLine);
            var app = CreateApplication(root, input, output, error);

            var exitCode = await app.RunAsync(
                ["help", "--pause"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains(
                "Presione Enter para cerrar...",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NormalCliInvocationDoesNotPause()
    {
        var root = CreateScratch();
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var input = new StringReader(string.Empty);
            var app = CreateApplication(root, input, output, error);

            var exitCode = await app.RunAsync(
                ["help"],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(
                "Presione Enter para cerrar...",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DesktopShortcutsUseInteractiveStartAndStop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateScratch();
        try
        {
            var launcherPath = Path.Combine(root, "LoomLCI.Launcher.exe");
            File.WriteAllBytes(launcherPath, []);

            var shortcuts = ShortcutCreator.CreateDesktopShortcuts(
                launcherPath,
                root);

            Assert.Equal(
                Path.Combine(root, "LoomLCI.lnk"),
                shortcuts.StartPath);
            Assert.Equal(
                Path.Combine(root, "Detener LoomLCI.lnk"),
                shortcuts.StopPath);

            var start = ReadShortcut(shortcuts.StartPath);
            Assert.Equal(launcherPath, start.TargetPath);
            Assert.Equal("start --pause", start.Arguments);
            Assert.Equal("Iniciar LoomLCI", start.Description);

            var stop = ReadShortcut(shortcuts.StopPath);
            Assert.Equal(launcherPath, stop.TargetPath);
            Assert.Equal("stop --pause", stop.Arguments);
            Assert.Equal("Detener LoomLCI", stop.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LauncherApplication CreateApplication(
        string root,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        var paths = new AppPaths(
            Path.Combine(root, "install"),
            Path.Combine(root, "data"));
        var client = new TunnelClient(new ProcessRunner());
        var setup = new SetupService(client);
        var updates = new UpdateService(
            paths,
            new FakeUpdateRuntimeControl());

        return new LauncherApplication(
            paths,
            client,
            setup,
            updates,
            input,
            output,
            error);
    }

    private static (
        string TargetPath,
        string Arguments,
        string Description) ReadShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException(
                "Windows Script Host no está disponible.");

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException(
                    "No se pudo crear WScript.Shell.");

            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(path);
            dynamic dynamicShortcut = shortcut;

            return (
                (string)dynamicShortcut.TargetPath,
                (string)dynamicShortcut.Arguments,
                (string)dynamicShortcut.Description);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
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
