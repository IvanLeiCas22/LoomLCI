using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LoomLCI.IntegrationTests;

public sealed class McpStdioTests
{
    [Fact]
    public async Task StdioAdapterAdvertisesServerInstructions()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(hostDll);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            const string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"loom-contract-test\",\"version\":\"1.0\"}}}";
            await process.StandardInput.WriteLineAsync(initialize);
            await process.StandardInput.FlushAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var responseLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(responseLine));

            using var response = JsonDocument.Parse(responseLine);
            var result = GetRequiredProperty(response.RootElement, "result");
            var instructions = GetRequiredProperty(result, "instructions").GetString();

            Assert.NotNull(instructions);
            Assert.Contains("not a sandbox", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Prefer structured LoomLCI filesystem capabilities", instructions, StringComparison.Ordinal);
            Assert.Contains("opaque values", instructions, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task StdioAdapterExposesSelfDescribingToolContracts()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI contract integration test",
            Command = "dotnet",
            Arguments = [hostDll],
            WorkingDirectory = repoRoot,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        var listTree = Assert.Single(tools, tool => tool.Name == "filesystem_list_tree");
        Assert.Equal("List directory tree", listTree.ProtocolTool.Title);
        Assert.Contains("discover project structure", listTree.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generated/infrastructure", listTree.Description, StringComparison.OrdinalIgnoreCase);
        var listProperties = GetRequiredProperty(listTree.JsonSchema, "properties");
        Assert.False(GetRequiredProperty(GetRequiredProperty(listProperties, "includeGenerated"), "default").GetBoolean());
        Assert.Contains("childrenExcluded", listTree.Description, StringComparison.Ordinal);
        Assert.Contains("nextCursor", listTree.Description, StringComparison.Ordinal);
        Assert.Contains("opaque", listTree.Description, StringComparison.OrdinalIgnoreCase);
        GetRequiredProperty(listProperties, "cursor");
        Assert.Equal(4096, GetRequiredProperty(GetRequiredProperty(listProperties, "cursor"), "maxLength").GetInt32());
        Assert.Contains("generated", listTree.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("excluded", listTree.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            64,
            GetRequiredProperty(GetRequiredProperty(listProperties, "excludeDirectories"), "maxItems").GetInt32());

        var findPaths = Assert.Single(tools, tool => tool.Name == "filesystem_find_paths");
        Assert.Equal("Find paths", findPaths.ProtocolTool.Title);
        Assert.Contains("does not inspect file contents", findPaths.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("childrenExcluded", findPaths.Description, StringComparison.Ordinal);
        Assert.Contains("nextCursor", findPaths.Description, StringComparison.Ordinal);
        Assert.Contains("opaque", findPaths.Description, StringComparison.OrdinalIgnoreCase);
        var findProperties = GetRequiredProperty(findPaths.JsonSchema, "properties");
        Assert.Equal(4096, GetRequiredProperty(GetRequiredProperty(findProperties, "cursor"), "maxLength").GetInt32());
        AssertSchemaRange(GetRequiredProperty(findProperties, "maxDepth"), 1, 32);
        AssertSchemaRange(GetRequiredProperty(findProperties, "maxResults"), 1, 1000);
        AssertSchemaEnum(GetRequiredProperty(findProperties, "matchMode"), "substring", "suffix");
        AssertSchemaEnum(GetRequiredProperty(findProperties, "type"), "any", "file", "directory", "symlink");
        GetRequiredProperty(findProperties, "includeGenerated");
        GetRequiredProperty(findProperties, "excludeDirectories");
        var queriesSchema = GetRequiredProperty(findProperties, "queries");
        Assert.Equal(1, GetRequiredProperty(queriesSchema, "minItems").GetInt32());
        Assert.Equal(32, GetRequiredProperty(queriesSchema, "maxItems").GetInt32());

        var searchText = Assert.Single(tools, tool => tool.Name == "filesystem_search_text");
        Assert.Contains("one to 32 literal text queries", searchText.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grouped by matching physical line", searchText.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nextCursor", searchText.Description, StringComparison.Ordinal);
        Assert.Contains("skippedBinaryFileCount", searchText.Description, StringComparison.Ordinal);
        Assert.Contains("64 MiB per page", searchText.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bounded excerpts", searchText.Description, StringComparison.OrdinalIgnoreCase);
        var searchProperties = GetRequiredProperty(searchText.JsonSchema, "properties");
        Assert.Equal(4096, GetRequiredProperty(GetRequiredProperty(searchProperties, "cursor"), "maxLength").GetInt32());
        var searchQueries = GetRequiredProperty(searchProperties, "queries");
        Assert.Equal(1, GetRequiredProperty(searchQueries, "minItems").GetInt32());
        Assert.Equal(32, GetRequiredProperty(searchQueries, "maxItems").GetInt32());
        Assert.False(searchProperties.TryGetProperty("query", out _));
        GetRequiredProperty(searchProperties, "includeGenerated");
        GetRequiredProperty(searchProperties, "excludeDirectories");

        var readFiles = Assert.Single(tools, tool => tool.Name == "filesystem_read_files");
        Assert.Contains("bounded ranges", readFiles.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("16 MiB", readFiles.Description, StringComparison.OrdinalIgnoreCase);
        var readProperties = GetRequiredProperty(readFiles.JsonSchema, "properties");
        var filesSchema = GetRequiredProperty(readProperties, "files");
        Assert.Equal(1, GetRequiredProperty(filesSchema, "minItems").GetInt32());
        Assert.Equal(32, GetRequiredProperty(filesSchema, "maxItems").GetInt32());

        var applyPatch = Assert.Single(tools, tool => tool.Name == "filesystem_apply_patch");
        Assert.Equal("Apply file changes", applyPatch.ProtocolTool.Title);
        Assert.Contains("large or binary files", applyPatch.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(applyPatch.ProtocolTool.Annotations?.OpenWorldHint ?? true);
        var applyProperties = GetRequiredProperty(applyPatch.JsonSchema, "properties");
        var changesItems = GetRequiredProperty(GetRequiredProperty(applyProperties, "changes"), "items");
        var changeProperties = GetRequiredProperty(changesItems, "properties");
        AssertSchemaEnum(GetRequiredProperty(changeProperties, "op"), "write", "replace", "delete", "move");

        var createWork = Assert.Single(tools, tool => tool.Name == "work_create");
        Assert.Contains("expire", createWork.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idle timeout", createWork.Description, StringComparison.OrdinalIgnoreCase);

        var closeWork = Assert.Single(tools, tool => tool.Name == "work_close");
        Assert.Contains("can no longer be inspected", closeWork.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("process_read", closeWork.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before closing", closeWork.Description, StringComparison.OrdinalIgnoreCase);

        var startProcess = Assert.Single(tools, tool => tool.Name == "process_start");
        Assert.Equal("Start process", startProcess.ProtocolTool.Title);
        Assert.Contains("cmd.exe", startProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ioMode=terminal", startProcess.Description, StringComparison.Ordinal);
        Assert.Contains("postExitRetentionSeconds", startProcess.Description, StringComparison.Ordinal);
        Assert.Contains("does not expire", startProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(startProcess.ProtocolTool.Annotations?.OpenWorldHint ?? true);
        var processStartProperties = GetRequiredProperty(startProcess.JsonSchema, "properties");
        var independentDescription = GetRequiredProperty(
            GetRequiredProperty(processStartProperties, "independent"),
            "description").GetString();
        Assert.Contains("remains Loom-managed", independentDescription, StringComparison.OrdinalIgnoreCase);
        AssertSchemaEnum(GetRequiredProperty(processStartProperties, "ioMode"), "pipes", "terminal");
        AssertSchemaRange(GetRequiredProperty(processStartProperties, "terminalColumns"), 1, short.MaxValue);
        AssertSchemaRange(GetRequiredProperty(processStartProperties, "terminalRows"), 1, short.MaxValue);

        var writeProcess = Assert.Single(tools, tool => tool.Name == "process_write");
        Assert.Contains("Ctrl+C", writeProcess.Description, StringComparison.Ordinal);
        Assert.Contains("\\u0003", writeProcess.Description, StringComparison.Ordinal);
        var processWriteProperties = GetRequiredProperty(writeProcess.JsonSchema, "properties");
        var writeTextDescription = GetRequiredProperty(
            GetRequiredProperty(processWriteProperties, "text"),
            "description").GetString();
        Assert.Contains("Ctrl+C", writeTextDescription, StringComparison.Ordinal);
        Assert.Contains("\\u0003", writeTextDescription, StringComparison.Ordinal);

        var statusProcess = Assert.Single(tools, tool => tool.Name == "process_status");
        Assert.Contains("retentionExpiresAt", statusProcess.Description, StringComparison.Ordinal);
        Assert.Contains("refreshes", statusProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(statusProcess.ProtocolTool.Annotations?.IdempotentHint ?? true);

        var terminateProcess = Assert.Single(tools, tool => tool.Name == "process_terminate");
        Assert.Contains("idempotent", terminateProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refreshes", terminateProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(terminateProcess.ProtocolTool.Annotations?.IdempotentHint ?? true);

        var readProcess = Assert.Single(tools, tool => tool.Name == "process_read");
        Assert.Contains("absolute UTF-16 positions", readProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retentionExpiresAt", readProcess.Description, StringComparison.Ordinal);
        Assert.Contains("retentionLimitReached", readProcess.Description, StringComparison.Ordinal);
        Assert.False(readProcess.ProtocolTool.Annotations?.IdempotentHint ?? true);
        Assert.Contains("64 MiB", readProcess.Description, StringComparison.OrdinalIgnoreCase);
        var processReadProperties = GetRequiredProperty(readProcess.JsonSchema, "properties");
        AssertSchemaRange(GetRequiredProperty(processReadProperties, "stdoutCursor"), 0, long.MaxValue);
        AssertSchemaRange(GetRequiredProperty(processReadProperties, "stderrCursor"), 0, long.MaxValue);
        AssertSchemaRange(GetRequiredProperty(processReadProperties, "terminalCursor"), 0, long.MaxValue);
        AssertSchemaRange(GetRequiredProperty(processReadProperties, "maxChars"), 1, 1048576);

        var resizeProcess = Assert.Single(tools, tool => tool.Name == "process_resize");
        Assert.Contains("terminal-mode", resizeProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(resizeProcess.ProtocolTool.Annotations?.IdempotentHint ?? false);
        var resizeProperties = GetRequiredProperty(resizeProcess.JsonSchema, "properties");
        AssertSchemaRange(GetRequiredProperty(resizeProperties, "columns"), 1, short.MaxValue);
        AssertSchemaRange(GetRequiredProperty(resizeProperties, "rows"), 1, short.MaxValue);
    }

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
        Assert.Contains("process_resize", toolNames);
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
        var invalidText = GetSingleTextContent(invalid);
        Assert.StartsWith("not_found: ", invalidText, StringComparison.Ordinal);
        Assert.Contains("proc_not-a-real-handle", invalidText, StringComparison.Ordinal);
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

        Assert.Equal(
            "Tool completed successfully. Structured result attached.",
            GetSingleTextContent(create));
        Assert.DoesNotContain(repoRoot, GetSingleTextContent(create), StringComparison.OrdinalIgnoreCase);
        var createRoot = GetStructured(create.StructuredContent);
        Assert.True(GetRequiredProperty(createRoot, "ok").GetBoolean());
        var createResult = GetRequiredProperty(createRoot, "result");
        var workId = GetRequiredProperty(createResult, "workId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workId));
        Assert.Equal(3600, GetRequiredProperty(createResult, "idleTimeoutSeconds").GetInt64());

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
        var startResult = GetRequiredProperty(startRoot, "result");
        Assert.Equal("pipes", GetRequiredProperty(startResult, "ioMode").GetString());
        Assert.Equal(900, GetRequiredProperty(startResult, "postExitRetentionSeconds").GetInt64());
        var processHandle = GetRequiredProperty(
            startResult,
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
            Assert.Equal("pipes", GetRequiredProperty(result, "ioMode").GetString());
            var state = GetRequiredProperty(result, "state").GetString();

            if (state is "exited" or "terminated")
            {
                Assert.Equal(3, GetRequiredProperty(result, "exitCode").GetInt32());
                Assert.Equal(
                    JsonValueKind.String,
                    GetRequiredProperty(result, "retentionExpiresAt").ValueKind);
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
        Assert.Equal("pipes", GetRequiredProperty(readResult, "ioMode").GetString());
        var readProcessState = GetRequiredProperty(readResult, "process");
        Assert.Equal(
            JsonValueKind.String,
            GetRequiredProperty(readProcessState, "retentionExpiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, GetRequiredProperty(readResult, "terminal").ValueKind);
        var stdout = GetRequiredProperty(readResult, "stdout");
        Assert.True(GetRequiredProperty(stdout, "retainedUntilCursor").GetInt64() >= 0);
        Assert.True(GetRequiredProperty(stdout, "observedUntilCursor").GetInt64() >= 0);
        Assert.False(GetRequiredProperty(stdout, "retentionLimitReached").GetBoolean());
        var chunks = GetRequiredProperty(stdout, "chunks");

        var output = string.Concat(
            chunks.EnumerateArray().Select(chunk => GetRequiredProperty(chunk, "text").GetString()));

        Assert.Contains("mcp-roundtrip", output, StringComparison.Ordinal);

        var resize = await client.CallToolAsync(
            "process_resize",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle,
                ["columns"] = 100,
                ["rows"] = 30
            });

        var resizeRoot = GetStructured(resize.StructuredContent);
        Assert.False(GetRequiredProperty(resizeRoot, "ok").GetBoolean());
        Assert.Equal(
            "unsupported",
            GetRequiredProperty(
                GetRequiredProperty(resizeRoot, "error"),
                "code").GetString());

        var close = await client.CallToolAsync(
            "work_close",
            new Dictionary<string, object?> { ["workId"] = workId });

        var closeRoot = GetStructured(close.StructuredContent);
        Assert.True(GetRequiredProperty(closeRoot, "ok").GetBoolean());
    }


    [Fact]
    public async Task StdioAdapterCanRunInteractiveTerminalProcess()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI terminal integration test",
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
                ["baseDirectory"] = repoRoot,
                ["label"] = "terminal-integration-test"
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
                ["executable"] = "powershell.exe",
                ["arguments"] = new[]
                {
                    "-NoProfile",
                    "-Command",
                    "$line=[Console]::ReadLine(); " +
                    "[Console]::WriteLine('MCP=' + $line); " +
                    "[Console]::WriteLine('SIZE=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight)"
                },
                ["workId"] = workId,
                ["ioMode"] = "terminal",
                ["terminalColumns"] = 80,
                ["terminalRows"] = 24
            });

        var startRoot = GetStructured(start.StructuredContent);
        Assert.True(GetRequiredProperty(startRoot, "ok").GetBoolean());
        var startResult = GetRequiredProperty(startRoot, "result");
        Assert.Equal("terminal", GetRequiredProperty(startResult, "ioMode").GetString());
        var processHandle = GetRequiredProperty(startResult, "processHandle").GetString();
        Assert.False(string.IsNullOrWhiteSpace(processHandle));

        var resize = await client.CallToolAsync(
            "process_resize",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle,
                ["columns"] = 100,
                ["rows"] = 30
            });
        Assert.True(
            GetRequiredProperty(
                GetStructured(resize.StructuredContent),
                "ok").GetBoolean());

        var write = await client.CallToolAsync(
            "process_write",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle,
                ["text"] = "mcp-terminal\r"
            });
        Assert.True(
            GetRequiredProperty(
                GetStructured(write.StructuredContent),
                "ok").GetBoolean());

        var terminalText = string.Empty;
        for (var i = 0; i < 200; i++)
        {
            var read = await client.CallToolAsync(
                "process_read",
                new Dictionary<string, object?>
                {
                    ["processHandle"] = processHandle,
                    ["terminalCursor"] = 0L
                });

            var readRoot = GetStructured(read.StructuredContent);
            Assert.True(GetRequiredProperty(readRoot, "ok").GetBoolean());
            var result = GetRequiredProperty(readRoot, "result");
            Assert.Equal("terminal", GetRequiredProperty(result, "ioMode").GetString());
            Assert.Equal(JsonValueKind.Null, GetRequiredProperty(result, "stdout").ValueKind);
            Assert.Equal(JsonValueKind.Null, GetRequiredProperty(result, "stderr").ValueKind);

            var terminal = GetRequiredProperty(result, "terminal");
            var chunks = GetRequiredProperty(terminal, "chunks");
            terminalText = string.Concat(
                chunks.EnumerateArray()
                    .Select(chunk => GetRequiredProperty(chunk, "text").GetString()));

            if (terminalText.Contains("MCP=mcp-terminal", StringComparison.Ordinal) &&
                terminalText.Contains("SIZE=100x30", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Contains("MCP=mcp-terminal", terminalText, StringComparison.Ordinal);
        Assert.Contains("SIZE=100x30", terminalText, StringComparison.Ordinal);

        JsonElement statusResult = default;
        for (var i = 0; i < 200; i++)
        {
            var status = await client.CallToolAsync(
                "process_status",
                new Dictionary<string, object?> { ["processHandle"] = processHandle });
            var statusRoot = GetStructured(status.StructuredContent);
            Assert.True(GetRequiredProperty(statusRoot, "ok").GetBoolean());
            statusResult = GetRequiredProperty(statusRoot, "result");

            if (GetRequiredProperty(statusResult, "state").GetString()
                is "exited" or "terminated")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Equal("exited", GetRequiredProperty(statusResult, "state").GetString());
        Assert.Equal(0, GetRequiredProperty(statusResult, "exitCode").GetInt32());

        var close = await client.CallToolAsync(
            "work_close",
            new Dictionary<string, object?> { ["workId"] = workId });
        Assert.True(
            GetRequiredProperty(
                GetStructured(close.StructuredContent),
                "ok").GetBoolean());
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
                        },
                        new Dictionary<string, object?>
                        {
                            ["op"] = "write",
                            ["path"] = "notes/other.txt",
                            ["content"] = "secondary"
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
                    ["workId"] = workId,
                    ["maxEntries"] = 1
                });
            var listRoot = GetStructured(list.StructuredContent);
            Assert.True(GetRequiredProperty(listRoot, "ok").GetBoolean());
            var listResult = GetRequiredProperty(listRoot, "result");
            var entries = GetRequiredProperty(listResult, "entries");
            Assert.Equal("notes", GetRequiredProperty(Assert.Single(entries.EnumerateArray()), "path").GetString());
            var listCursor = GetRequiredProperty(listResult, "nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(listCursor));

            var listNext = await client.CallToolAsync(
                "filesystem_list_tree",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["workId"] = workId,
                    ["maxEntries"] = 2,
                    ["cursor"] = listCursor
                });
            var listNextResult = GetRequiredProperty(GetStructured(listNext.StructuredContent), "result");
            Assert.Equal(
                ["notes/note.txt", "notes/other.txt"],
                GetRequiredProperty(listNextResult, "entries")
                    .EnumerateArray()
                    .Select(entry => GetRequiredProperty(entry, "path").GetString()));
            Assert.False(listNextResult.TryGetProperty("nextCursor", out _));

            var found = await client.CallToolAsync(
                "filesystem_find_paths",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["queries"] = new[] { ".txt" },
                    ["matchMode"] = "suffix",
                    ["type"] = "file",
                    ["workId"] = workId,
                    ["maxResults"] = 1
                });
            var foundRoot = GetStructured(found.StructuredContent);
            Assert.True(GetRequiredProperty(foundRoot, "ok").GetBoolean());
            var foundResult = GetRequiredProperty(foundRoot, "result");
            Assert.Single(GetRequiredProperty(foundResult, "matches").EnumerateArray());
            var findCursor = GetRequiredProperty(foundResult, "nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(findCursor));

            var foundNext = await client.CallToolAsync(
                "filesystem_find_paths",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["queries"] = new[] { ".txt" },
                    ["matchMode"] = "suffix",
                    ["type"] = "file",
                    ["workId"] = workId,
                    ["maxResults"] = 2,
                    ["cursor"] = findCursor
                });
            var foundNextResult = GetRequiredProperty(GetStructured(foundNext.StructuredContent), "result");
            Assert.Single(GetRequiredProperty(foundNextResult, "matches").EnumerateArray());
            Assert.False(foundNextResult.TryGetProperty("nextCursor", out _));

            var searched = await client.CallToolAsync(
                "filesystem_search_text",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["queries"] = new[] { "needle", "secondary" },
                    ["workId"] = workId,
                    ["maxResults"] = 1,
                    ["contextLines"] = 0
                });
            var searchedRoot = GetStructured(searched.StructuredContent);
            Assert.True(GetRequiredProperty(searchedRoot, "ok").GetBoolean());
            var searchedResult = GetRequiredProperty(searchedRoot, "result");
            Assert.True(GetRequiredProperty(searchedResult, "resultLimitReached").GetBoolean());
            var searchedQueries = GetRequiredProperty(searchedResult, "queries");
            Assert.Equal(2, searchedQueries.GetArrayLength());
            var textMatch = Assert.Single(GetRequiredProperty(searchedResult, "matches").EnumerateArray());
            Assert.Equal(1, GetRequiredProperty(textMatch, "textStartColumn").GetInt32());
            Assert.False(GetRequiredProperty(textMatch, "textTruncated").GetBoolean());
            var searchCursor = GetRequiredProperty(searchedResult, "nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(searchCursor));

            var searchedNext = await client.CallToolAsync(
                "filesystem_search_text",
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["queries"] = new[] { "needle", "secondary" },
                    ["workId"] = workId,
                    ["maxResults"] = 2,
                    ["contextLines"] = 0,
                    ["cursor"] = searchCursor
                });
            var searchedNextResult = GetRequiredProperty(
                GetStructured(searchedNext.StructuredContent),
                "result");
            var nextTextMatch = Assert.Single(
                GetRequiredProperty(searchedNextResult, "matches").EnumerateArray());
            var queryMatch = Assert.Single(
                GetRequiredProperty(nextTextMatch, "queryMatches").EnumerateArray());
            Assert.Equal("secondary", GetRequiredProperty(queryMatch, "query").GetString());
            Assert.Equal(1, GetRequiredProperty(queryMatch, "column").GetInt32());
            Assert.False(searchedNextResult.TryGetProperty("nextCursor", out _));

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
            Assert.Equal(
                "Tool completed successfully. Structured result attached.",
                GetSingleTextContent(read));
            Assert.DoesNotContain("alpha needle", GetSingleTextContent(read), StringComparison.Ordinal);
            var readRoot = GetStructured(read.StructuredContent);
            Assert.True(GetRequiredProperty(readRoot, "ok").GetBoolean());
            var files = GetRequiredProperty(
                GetRequiredProperty(readRoot, "result"),
                "files");
            var file = Assert.Single(files.EnumerateArray());
            Assert.Equal("alpha needle", GetRequiredProperty(file, "text").GetString());
            Assert.False(GetRequiredProperty(file, "hasMoreBefore").GetBoolean());
            Assert.False(GetRequiredProperty(file, "hasMoreAfter").GetBoolean());
            Assert.False(file.TryGetProperty("truncated", out _));

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

    private static string GetSingleTextContent(CallToolResult result)
    {
        var content = Assert.Single(result.Content);
        return Assert.IsType<TextContentBlock>(content).Text;
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

    private static void AssertSchemaRange(JsonElement schema, long minimum, long maximum)
    {
        Assert.Equal((double)minimum, GetRequiredProperty(schema, "minimum").GetDouble());
        Assert.Equal((double)maximum, GetRequiredProperty(schema, "maximum").GetDouble());
    }

    private static void AssertSchemaEnum(JsonElement schema, params string[] expected)
    {
        var actual = GetRequiredProperty(schema, "enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Equal(expected, actual);
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
