using System.Text.Json;
using ModelContextProtocol.Client;

namespace LoomLCI.IntegrationTests;

public sealed class McpStdioTests
{
    [Fact]
    public async Task StdioAdapterCanCreateWorkRunProcessAndReadOutput()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);

        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI integration test",
            Command = "dotnet",
            Arguments = [hostDll],
            WorkingDirectory = repoRoot,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });

        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        var toolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("work_create", toolNames);
        Assert.Contains("work_close", toolNames);
        Assert.Contains("process_start", toolNames);
        Assert.Contains("process_status", toolNames);
        Assert.Contains("process_read", toolNames);
        Assert.Contains("process_write", toolNames);
        Assert.Contains("process_terminate", toolNames);
        Assert.True(
            toolNames.Contains("filesystem_list_tree"),
            $"Host '{hostDll}' did not expose filesystem_list_tree. Tools: {string.Join(", ", toolNames.OrderBy(x => x))}");
        Assert.Contains("filesystem_find_paths", toolNames);
        Assert.Contains("filesystem_search_text", toolNames);
        Assert.Contains("filesystem_read_files", toolNames);
        Assert.Contains("filesystem_apply_patch", toolNames);
        Assert.Contains("filesystem_manage_directory", toolNames);

        var invalid = await client.CallToolAsync(
            "process_status",
            new Dictionary<string, object?> { ["processHandle"] = "proc_not-a-real-handle" });

        Assert.True(invalid.IsError is true);
        var invalidRoot = GetStructured(invalid.StructuredContent);
        Assert.False(GetRequiredProperty(invalidRoot, "ok").GetBoolean());
        Assert.Equal(
            "not_found",
            GetRequiredProperty(
                GetRequiredProperty(invalidRoot, "error"),
                "code").GetString());

        var create = await client.CallToolAsync(
            "work_create",
            new Dictionary<string, object?>
            {
                ["baseDirectory"] = repoRoot,
                ["label"] = "integration-test"
            });

        var createRoot = GetStructured(create.StructuredContent);
        Assert.True(GetRequiredProperty(createRoot, "ok").GetBoolean());
        var workId = GetRequiredProperty(
            GetRequiredProperty(createRoot, "result"),
            "workId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workId));

        var start = await client.CallToolAsync(
            "process_start",
            new Dictionary<string, object?>
            {
                ["executable"] = "cmd.exe",
                ["arguments"] = new[] { "/d", "/s", "/c", "echo mcp-roundtrip & exit /b 3" },
                ["workId"] = workId
            });

        var startRoot = GetStructured(start.StructuredContent);
        Assert.True(GetRequiredProperty(startRoot, "ok").GetBoolean());
        var processHandle = GetRequiredProperty(
            GetRequiredProperty(startRoot, "result"),
            "processHandle").GetString();
        Assert.False(string.IsNullOrWhiteSpace(processHandle));

        JsonElement statusRoot = default;
        for (var i = 0; i < 100; i++)
        {
            var status = await client.CallToolAsync(
                "process_status",
                new Dictionary<string, object?> { ["processHandle"] = processHandle });

            statusRoot = GetStructured(status.StructuredContent);
            Assert.True(GetRequiredProperty(statusRoot, "ok").GetBoolean());
            var result = GetRequiredProperty(statusRoot, "result");
            var state = GetRequiredProperty(result, "state").GetString();

            if (state is "exited" or "terminated")
            {
                Assert.Equal(3, GetRequiredProperty(result, "exitCode").GetInt32());
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotEqual(JsonValueKind.Undefined, statusRoot.ValueKind);

        var read = await client.CallToolAsync(
            "process_read",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle,
                ["stdoutCursor"] = 0L,
                ["stderrCursor"] = 0L
            });

        var readRoot = GetStructured(read.StructuredContent);
        Assert.True(GetRequiredProperty(readRoot, "ok").GetBoolean());
        var readResult = GetRequiredProperty(readRoot, "result");
        var stdout = GetRequiredProperty(readResult, "stdout");
        var chunks = GetRequiredProperty(stdout, "chunks");

        var output = string.Concat(
            chunks.EnumerateArray().Select(chunk => GetRequiredProperty(chunk, "text").GetString()));

        Assert.Contains("mcp-roundtrip", output, StringComparison.Ordinal);

        var close = await client.CallToolAsync(
            "work_close",
            new Dictionary<string, object?> { ["workId"] = workId });

        var closeRoot = GetStructured(close.StructuredContent);
        Assert.True(GetRequiredProperty(closeRoot, "ok").GetBoolean());
    }

    [Fact]
    public async Task StdioAdapterCanRoundTripFilesystem()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var scratch = Path.Combine(Path.GetTempPath(), "LoomLCI.McpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "LoomLCI filesystem integration test",
                Command = "dotnet",
                Arguments = [hostDll],
                WorkingDirectory = repoRoot,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });

            await using var client = await McpClient.CreateAsync(transport);

            var create = await client.CallToolAsync(
                "work_create",
                new Dictionary<string, object?>
                {
                    ["baseDirectory"] = scratch,
                    ["label"] = "filesystem-integration-test"
                });

            var createRoot = GetStructured(create.StructuredContent);
            Assert.True(GetRequiredProperty(createRoot, "ok").GetBoolean());
            var workId = GetRequiredProperty(
                GetRequiredProperty(createRoot, "result"),
                "workId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));

            var mkdir = await client.CallToolAsync(
                "filesystem_manage_directory",
                new Dictionary<string, object?>
                {
                    ["action"] = "create",
                    ["path"] = "notes",
                    ["workId"] = workId
                });
            Assert.True(GetRequiredProperty(GetStructured(mkdir.StructuredContent), "ok").GetBoolean());

            var write = await client.CallToolAsync(
                "filesystem_apply_patch",
                new Dictionary<string, object?>
                {
                    ["changes"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["op"] = "write",
                            ["path"] = "notes/note.txt",
                            ["content"] = "alpha needle"
                        }
                    },
                    ["workId"] = workId
                });
            Assert.True(GetRequiredProperty(GetStructured(write.StructuredContent), "ok").GetBoolean());

            var list = await client.CallToolAsync(
                "filesystem_list_tree",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["workId"] = workId
                });
            var listRoot = GetStructured(list.StructuredContent);
            Assert.True(GetRequiredProperty(listRoot, "ok").GetBoolean());
            var entries = GetRequiredProperty(
                GetRequiredProperty(listRoot, "result"),
                "entries");
            Assert.Contains(
                entries.EnumerateArray(),
                entry => GetRequiredProperty(entry, "path").GetString() == "notes/note.txt");

            var found = await client.CallToolAsync(
                "filesystem_find_paths",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["queries"] = new[] { ".txt" },
                    ["matchMode"] = "suffix",
                    ["type"] = "file",
                    ["workId"] = workId
                });
            var foundRoot = GetStructured(found.StructuredContent);
            Assert.True(GetRequiredProperty(foundRoot, "ok").GetBoolean());
            var matches = GetRequiredProperty(
                GetRequiredProperty(foundRoot, "result"),
                "matches");
            Assert.Single(matches.EnumerateArray());

            var searched = await client.CallToolAsync(
                "filesystem_search_text",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["query"] = "needle",
                    ["workId"] = workId
                });
            var searchedRoot = GetStructured(searched.StructuredContent);
            Assert.True(GetRequiredProperty(searchedRoot, "ok").GetBoolean());
            var textMatches = GetRequiredProperty(
                GetRequiredProperty(searchedRoot, "result"),
                "matches");
            Assert.Single(textMatches.EnumerateArray());

            var read = await client.CallToolAsync(
                "filesystem_read_files",
                new Dictionary<string, object?>
                {
                    ["files"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["path"] = "notes/note.txt"
                        }
                    },
                    ["workId"] = workId
                });
            var readRoot = GetStructured(read.StructuredContent);
            Assert.True(GetRequiredProperty(readRoot, "ok").GetBoolean());
            var files = GetRequiredProperty(
                GetRequiredProperty(readRoot, "result"),
                "files");
            var file = Assert.Single(files.EnumerateArray());
            Assert.Equal("alpha needle", GetRequiredProperty(file, "text").GetString());

            var replace = await client.CallToolAsync(
                "filesystem_apply_patch",
                new Dictionary<string, object?>
                {
                    ["changes"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["op"] = "replace",
                            ["path"] = "notes/note.txt",
                            ["oldText"] = "alpha",
                            ["newText"] = "beta"
                        }
                    },
                    ["workId"] = workId
                });
            Assert.True(GetRequiredProperty(GetStructured(replace.StructuredContent), "ok").GetBoolean());
            Assert.Equal("beta needle", await File.ReadAllTextAsync(Path.Combine(scratch, "notes", "note.txt")));

            var close = await client.CallToolAsync(
                "work_close",
                new Dictionary<string, object?> { ["workId"] = workId });
            Assert.True(GetRequiredProperty(GetStructured(close.StructuredContent), "ok").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static string GetHostDll(string repoRoot)
    {
        var hostDll = Environment.GetEnvironmentVariable("LOOMLCI_TEST_HOST_DLL");
        return string.IsNullOrWhiteSpace(hostDll)
            ? Path.Combine(
                repoRoot,
                "src",
                "LoomLCI.Host",
                "bin",
                "Debug",
                "net10.0",
                "LoomLCI.Host.dll")
            : hostDll;
    }

    private static JsonElement GetStructured(JsonElement? content)
    {
        Assert.True(content.HasValue, "Expected structuredContent from MCP tool.");
        return content.Value;
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value))
        {
            return value;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new Xunit.Sdk.XunitException($"Property '{name}' not found in: {element}");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LoomLCI.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate LoomLCI repository root.");
    }
}
