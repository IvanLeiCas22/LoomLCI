using LoomLCI.Core.Observability;

namespace LoomLCI.Launcher.Tests;

public sealed class DiagnosticsLogTests
{
    [Fact]
    public void OptInIsFailClosedAndConfigSurvivesToggle()
    {
        var root = Scratch();
        try
        {
            var record = new DiagnosticRecord(DateTimeOffset.UtcNow, "launcher", "LauncherCommand",
                "start", "success");
            Assert.False(DiagnosticsLog.IsEnabled(root));
            Assert.False(DiagnosticsLog.TryAppend(root, record));
            Assert.False(Directory.Exists(DiagnosticsLog.LogsPath(root)));
            DiagnosticsLog.SetEnabled(root, true);
            Assert.True(DiagnosticsLog.IsEnabled(root));
            Assert.True(DiagnosticsLog.TryAppend(root, record));
            Assert.Single(DiagnosticsLog.ReadTail(root, 20));
            DiagnosticsLog.SetEnabled(root, false);
            Assert.False(DiagnosticsLog.TryAppend(root, record));
            Assert.Single(DiagnosticsLog.ReadTail(root, 20));
            File.WriteAllText(DiagnosticsLog.ConfigPath(root), "{broken");
            Assert.False(DiagnosticsLog.IsEnabled(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsSensitiveSymbolsAndInvalidComponents()
    {
        var root = Scratch();
        try
        {
            DiagnosticsLog.SetEnabled(root, true);
            Assert.False(DiagnosticsLog.TryAppend(root,
                new DiagnosticRecord(DateTimeOffset.UtcNow, "launcher", "LauncherCommand",
                    Operation: "token=SECRETVALUE")));
            Assert.False(DiagnosticsLog.TryAppend(root,
                new DiagnosticRecord(DateTimeOffset.UtcNow, "unsanitized", "Test")));
            Assert.Empty(DiagnosticsLog.ReadTail(root, 20));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RotatesPrunesOldRecordsAndPreservesForeignFiles()
    {
        var root = Scratch();
        try
        {
            DiagnosticsLog.SetEnabled(root, true);
            var dir = DiagnosticsLog.LogsPath(root);
            Directory.CreateDirectory(dir);
            var prefix = "launcher-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd") +
                "-" + Environment.ProcessId;
            var filled = Path.Combine(dir, prefix + "-0000.jsonl");
            File.WriteAllBytes(filled, new byte[DiagnosticsLog.MaxFileBytes]);
            var expired = Path.Combine(dir, "launcher-old-0000.jsonl");
            File.WriteAllText(expired, "{}\n");
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-9));
            var foreign = Path.Combine(dir, "other-stored.txt");
            File.WriteAllText(foreign, "Do not delete");
            Assert.True(DiagnosticsLog.TryAppend(root, new DiagnosticRecord(
                DateTimeOffset.UtcNow, "launcher", "UpdateStage", "update", "Prepared")));
            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(filled));
            Assert.True(File.Exists(Path.Combine(dir, prefix + "-0001.jsonl")));
            DiagnosticsLog.SetEnabled(root, false);
            Assert.Equal(2, DiagnosticsLog.Clear(root));
            Assert.True(File.Exists(foreign));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EnforcesQuotaPerComponent()
    {
        var root = Scratch();
        try
        {
            DiagnosticsLog.SetEnabled(root, true);
            var dir = DiagnosticsLog.LogsPath(root);
            Directory.CreateDirectory(dir);
            var prefix = "host-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd") +
                "-" + Environment.ProcessId;
            for (var i = 0; i < 4; i++)
            {
                var path = Path.Combine(dir, prefix + "-" + i.ToString("D4") + ".jsonl");
                File.WriteAllBytes(path, new byte[DiagnosticsLog.MaxFileBytes]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-i - 1));
            }
            Assert.True(DiagnosticsLog.TryAppend(root, new DiagnosticRecord(
                DateTimeOffset.UtcNow, "host", "InvocationStarted")));
            var usage = DiagnosticsLog.GetUsage(root);
            Assert.InRange(usage.Bytes, 0, DiagnosticsLog.MaxComponentBytes);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "LoomLCI.RB06.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
