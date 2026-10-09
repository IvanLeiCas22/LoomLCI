using LoomLCI.Core.Observability;
using ModelContextProtocol.Client;

namespace LoomLCI.IntegrationTests;

public sealed class DiagnosticsStdioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostOptInPersistsSafeEventsWithoutBreakingStdio(bool enabled)
    {
        var repo = FindRepoRoot();
        var host = Environment.GetEnvironmentVariable("LOOMLCI_TEST_HOST_DLL");
        if (string.IsNullOrWhiteSpace(host))
            host = Path.Combine(repo, "src", "LoomLCI.Host", "bin", "Release",
                "net10.0-windows10.0.19041.0", "LoomLCI.Host.dll");
        Assert.True(File.Exists(host), "Published or Release Host is missing.");
        var dataRoot = Path.Combine(Path.GetTempPath(), "LoomLCI.RB06.Mcp",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            if (enabled)
                DiagnosticsLog.SetEnabled(dataRoot, true);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "RB06 diagnostics opt-in",
                Command = "dotnet",
                Arguments = [host],
                WorkingDirectory = repo,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["LOOMLCI_DATA_ROOT"] = dataRoot
                },
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });
            await using (var client = await McpClient.CreateAsync(transport))
            {
                var tools = await client.ListToolsAsync();
                Assert.Contains(tools, tool => tool.Name == "work_create");
                var create = await client.CallToolAsync("work_create",
                    new Dictionary<string, object?> { ["label"] = "RB06_INTEGRATION" });
                Assert.True(create.IsError is not true);
                if (enabled)
                {
                    var observed = false;
                    for (var attempt = 0; attempt < 40 && !observed; attempt++)
                    {
                        await Task.Delay(100);
                        observed = DiagnosticsLog.ReadTail(dataRoot, 30)
                            .Any(line => line.Contains("WorkSessionCreated", StringComparison.Ordinal));
                    }
                    Assert.True(observed, "Host did not persist WorkSessionCreated.");
                    var content = string.Join("\n", DiagnosticsLog.ReadTail(dataRoot, 30));
                    Assert.DoesNotContain("RB06_INTEGRATION", content, StringComparison.Ordinal);
                }
            }
            if (!enabled)
                Assert.False(Directory.Exists(DiagnosticsLog.LogsPath(dataRoot)));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LoomLCI.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Missing LoomLCI root");
    }
}
