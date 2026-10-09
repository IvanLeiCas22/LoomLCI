using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoomLCI.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.IntegrationTests;

public sealed class McpStdioTests
{
    [Fact]
    public void WorkPlanRegistrationIsOptIn()
    {
        var disabledServices = new ServiceCollection();
        disabledServices.AddLoomMcpStdio(enableWorkPlan: false);

        var enabledServices = new ServiceCollection();
        enabledServices.AddLoomMcpStdio(enableWorkPlan: true);

        var disabledToolRegistrations = disabledServices.Count(
            descriptor => descriptor.ServiceType == typeof(McpServerTool));
        var enabledToolRegistrations = enabledServices.Count(
            descriptor => descriptor.ServiceType == typeof(McpServerTool));

        Assert.Equal(22, disabledToolRegistrations);
        Assert.Equal(25, enabledToolRegistrations);
    }

    [Fact]
    public async Task PluginContractSnapshotMatchesAdvertisedTools()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        var snapshotPath = Path.Combine(
            repoRoot,
            "plugin",
            "contract",
            "mcp-contract.json");
        var skillPath = Path.Combine(
            repoRoot,
            "plugin",
            "skills",
            "loomlci",
            "SKILL.md");

        Assert.True(File.Exists(snapshotPath), $"Plugin contract snapshot not found: {snapshotPath}");
        Assert.True(File.Exists(skillPath), $"Plugin skill not found: {skillPath}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI plugin contract regression",
            Command = "dotnet",
            Arguments = [hostDll],
            WorkingDirectory = repoRoot,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        using var snapshot = JsonDocument.Parse(
            await File.ReadAllTextAsync(snapshotPath));
        var snapshotTools = GetRequiredProperty(snapshot.RootElement, "tools")
            .EnumerateArray()
            .ToDictionary(
                element => GetRequiredProperty(element, "name").GetString()!,
                StringComparer.Ordinal);

        Assert.Equal(tools.Count, snapshotTools.Count);

        foreach (var tool in tools)
        {
            Assert.True(
                snapshotTools.TryGetValue(tool.Name, out var expected),
                $"Tool '{tool.Name}' is missing from plugin/contract/mcp-contract.json.");

            Assert.Equal(
                tool.ProtocolTool.Title,
                GetRequiredProperty(expected, "title").GetString());
            Assert.Equal(
                tool.Description,
                GetRequiredProperty(expected, "description").GetString());
            Assert.Equal(
                JsonValueKind.Object,
                GetRequiredProperty(expected, "inputSchema").ValueKind);
        }

        var skill = await File.ReadAllTextAsync(skillPath);
        var toolRefs = Regex.Matches(
                skill,
                @"\b(?:work|filesystem|process|python|computer)_[a-z0-9_]+\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var liveToolNames = tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            toolRefs,
            reference => Assert.Contains(reference, liveToolNames));
    }

    [Fact]
    public void PluginSourceDoesNotReintroduceLegacyDebugWiring()
    {
        var repoRoot = FindRepoRoot();
        var pluginRoot = Path.Combine(repoRoot, "plugin");
        var sourceFiles = new[]
        {
            Path.Combine(pluginRoot, "plugin.json"),
            Path.Combine(pluginRoot, "README.md"),
            Path.Combine(pluginRoot, "skills", "loomlci", "SKILL.md")
        };

        foreach (var path in sourceFiles)
        {
            Assert.True(File.Exists(path), $"Plugin source file not found: {path}");
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("C:\\Users\\", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bin\\Debug", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LoomLCI.Host.dll", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "Documents\\ProyectosPersonales\\LoomLCI\\src",
                text,
                StringComparison.OrdinalIgnoreCase);
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(sourceFiles[0]));
        Assert.False(manifest.RootElement.TryGetProperty("mcpServers", out _));
        Assert.Equal(
            "loomlci",
            GetRequiredProperty(manifest.RootElement, "name").GetString());
    }

    [Fact]
    public void PluginSkillTracksFinalPythonWorkflow()
    {
        var repoRoot = FindRepoRoot();
        var skillPath = Path.Combine(
            repoRoot,
            "plugin",
            "skills",
            "loomlci",
            "SKILL.md");

        Assert.True(File.Exists(skillPath), $"Plugin skill not found: {skillPath}");
        var skill = File.ReadAllText(skillPath);

        foreach (var marker in new[]
        {
            "python_execute",
            "python_packages_prepare",
            "python_reset",
            "loom.fs",
            "loom.process",
            "loom.display_image",
            "filesystem_view_image",
            "filesystem_render_pdf_page",
            "completamente en memoria",
            "imagen local",
            "MCP/tunnel",
            "Process top-level",
            "Independent",
            "ChatGPT Code Mode",
            "content_items",
            "type === \"image\"",
            "image(item)",
            "StructuredContent"
        })
        {
            Assert.Contains(marker, skill, StringComparison.OrdinalIgnoreCase);
        }
    }

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
            Assert.Contains("filesystem_view_image", instructions, StringComparison.Ordinal);
            Assert.Contains("filesystem_read_pdf", instructions, StringComparison.Ordinal);
            Assert.Contains("filesystem_render_pdf_page", instructions, StringComparison.Ordinal);
            Assert.Contains("python_execute", instructions, StringComparison.Ordinal);
            Assert.Contains("loom.display_image", instructions, StringComparison.Ordinal);
            Assert.Contains("9 MiB", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not automatically rerun", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Process capabilities", instructions, StringComparison.Ordinal);
            Assert.Contains("opaque values", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Work Plan", instructions, StringComparison.Ordinal);
            Assert.Contains("multiple meaningful phases", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("simple lookups", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("revision 0", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("meaningful milestones", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("work_plan_patch", instructions, StringComparison.Ordinal);
            Assert.Contains("reordering", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("on conflict", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not auto-merge", instructions, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("9 MiB", readFiles.Description, StringComparison.OrdinalIgnoreCase);
        var readProperties = GetRequiredProperty(readFiles.JsonSchema, "properties");
        var filesSchema = GetRequiredProperty(readProperties, "files");
        Assert.Equal(1, GetRequiredProperty(filesSchema, "minItems").GetInt32());
        Assert.Equal(32, GetRequiredProperty(filesSchema, "maxItems").GetInt32());

        var viewImage = Assert.Single(tools, tool => tool.Name == "filesystem_view_image");
        Assert.Equal("View local image", viewImage.ProtocolTool.Title);
        Assert.Contains("PNG, JPEG, or WebP", viewImage.Description, StringComparison.Ordinal);
        Assert.Contains("does not resize, convert, edit, or OCR", viewImage.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewImage.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.False(viewImage.ProtocolTool.Annotations?.DestructiveHint ?? true);
        Assert.True(viewImage.ProtocolTool.Annotations?.IdempotentHint ?? false);
        Assert.False(viewImage.ProtocolTool.Annotations?.OpenWorldHint ?? true);
        Assert.Null(viewImage.ProtocolTool.OutputSchema);
        var viewImageProperties = GetRequiredProperty(viewImage.JsonSchema, "properties");
        GetRequiredProperty(viewImageProperties, "path");
        GetRequiredProperty(viewImageProperties, "workId");
        var viewImageRequired = GetRequiredProperty(viewImage.JsonSchema, "required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("path", viewImageRequired);
        Assert.DoesNotContain("workId", viewImageRequired);

        var readPdf = Assert.Single(tools, tool => tool.Name == "filesystem_read_pdf");
        Assert.Equal("Read PDF text", readPdf.ProtocolTool.Title);
        Assert.Contains("crash-isolated worker", readPdf.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("64 MiB", readPdf.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not perform OCR", readPdf.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(readPdf.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.False(readPdf.ProtocolTool.Annotations?.DestructiveHint ?? true);
        Assert.True(readPdf.ProtocolTool.Annotations?.IdempotentHint ?? false);
        Assert.False(readPdf.ProtocolTool.Annotations?.OpenWorldHint ?? true);
        Assert.NotNull(readPdf.ProtocolTool.OutputSchema);
        var readPdfProperties = GetRequiredProperty(readPdf.JsonSchema, "properties");
        GetRequiredProperty(readPdfProperties, "path");
        GetRequiredProperty(readPdfProperties, "workId");
        AssertSchemaRange(GetRequiredProperty(readPdfProperties, "startPage"), 1, int.MaxValue);
        AssertSchemaRange(GetRequiredProperty(readPdfProperties, "maxPages"), 1, 25);
        Assert.Equal(1, GetRequiredProperty(GetRequiredProperty(readPdfProperties, "startPage"), "default").GetInt32());
        Assert.Equal(10, GetRequiredProperty(GetRequiredProperty(readPdfProperties, "maxPages"), "default").GetInt32());

        var renderPdf = Assert.Single(tools, tool => tool.Name == "filesystem_render_pdf_page");
        Assert.Equal("Render PDF page", renderPdf.ProtocolTool.Title);
        Assert.Contains("crash-isolated worker", renderPdf.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scanned/image-only", renderPdf.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aspect ratio", renderPdf.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(renderPdf.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.False(renderPdf.ProtocolTool.Annotations?.DestructiveHint ?? true);
        Assert.True(renderPdf.ProtocolTool.Annotations?.IdempotentHint ?? false);
        Assert.False(renderPdf.ProtocolTool.Annotations?.OpenWorldHint ?? true);
        Assert.Null(renderPdf.ProtocolTool.OutputSchema);
        var renderPdfProperties = GetRequiredProperty(renderPdf.JsonSchema, "properties");
        GetRequiredProperty(renderPdfProperties, "path");
        GetRequiredProperty(renderPdfProperties, "workId");
        AssertSchemaRange(GetRequiredProperty(renderPdfProperties, "page"), 1, int.MaxValue);
        AssertSchemaRange(GetRequiredProperty(renderPdfProperties, "maxWidth"), 256, 4096);
        AssertSchemaRange(GetRequiredProperty(renderPdfProperties, "maxHeight"), 256, 4096);
        Assert.Equal(1, GetRequiredProperty(GetRequiredProperty(renderPdfProperties, "page"), "default").GetInt32());
        Assert.Equal(1800, GetRequiredProperty(GetRequiredProperty(renderPdfProperties, "maxWidth"), "default").GetInt32());
        Assert.Equal(2400, GetRequiredProperty(GetRequiredProperty(renderPdfProperties, "maxHeight"), "default").GetInt32());

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
        Assert.Contains("cleanup_failed", closeWork.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", closeWork.Description, StringComparison.OrdinalIgnoreCase);
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

        var runProcess = Assert.Single(tools, tool => tool.Name == "process_run");
        Assert.Equal("Run short process", runProcess.ProtocolTool.Title);
        Assert.Contains("non-interactive", runProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-zero exit code", runProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not return a process handle", runProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(runProcess.ProtocolTool.Annotations?.ReadOnlyHint ?? true);
        Assert.True(runProcess.ProtocolTool.Annotations?.DestructiveHint ?? false);
        Assert.False(runProcess.ProtocolTool.Annotations?.IdempotentHint ?? true);
        Assert.False(runProcess.ProtocolTool.Annotations?.OpenWorldHint ?? true);
        var processRunProperties = GetRequiredProperty(runProcess.JsonSchema, "properties");
        AssertSchemaRange(GetRequiredProperty(processRunProperties, "timeoutSeconds"), 1, 600);
        AssertSchemaRange(GetRequiredProperty(processRunProperties, "maxOutputChars"), 1, 1048576);
        Assert.False(processRunProperties.TryGetProperty("ioMode", out _));
        Assert.False(processRunProperties.TryGetProperty("independent", out _));

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

        var releaseProcess = Assert.Single(tools, tool => tool.Name == "process_release");
        Assert.Contains("does not stop a live process", releaseProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("final output", releaseProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already closed or expired", releaseProcess.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(releaseProcess.ProtocolTool.Annotations?.IdempotentHint ?? false);
        Assert.True(releaseProcess.ProtocolTool.Annotations?.DestructiveHint ?? false);

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

        var executePython = Assert.Single(tools, tool => tool.Name == "python_execute");
        Assert.Equal("Execute Python", executePython.ProtocolTool.Title);
        Assert.Contains("persistent", executePython.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("input()", executePython.Description, StringComparison.Ordinal);
        Assert.Contains("256 KiB", executePython.Description, StringComparison.Ordinal);
        Assert.Contains("loom.display_image", executePython.Description, StringComparison.Ordinal);
        Assert.Contains("PNG, JPEG, or WebP", executePython.Description, StringComparison.Ordinal);
        Assert.Contains("9 MiB", executePython.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("executionCompleted=true", executePython.Description, StringComparison.Ordinal);
        Assert.Null(executePython.ProtocolTool.OutputSchema);
        Assert.True(executePython.ProtocolTool.Annotations?.DestructiveHint ?? false);
        Assert.False(executePython.ProtocolTool.Annotations?.ReadOnlyHint ?? true);
        Assert.False(executePython.ProtocolTool.Annotations?.IdempotentHint ?? true);
        Assert.True(executePython.ProtocolTool.Annotations?.OpenWorldHint ?? false);

        var pythonProperties = GetRequiredProperty(executePython.JsonSchema, "properties");
        AssertSchemaRange(GetRequiredProperty(pythonProperties, "timeoutSeconds"), 1, 600);
        AssertSchemaRange(GetRequiredProperty(pythonProperties, "maxOutputChars"), 1, 1048576);
        var pythonRequired = GetRequiredProperty(executePython.JsonSchema, "required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("workId", pythonRequired);
        Assert.Contains("code", pythonRequired);

        var preparePythonPackages = Assert.Single(
            tools,
            tool => tool.Name == "python_packages_prepare");
        Assert.Equal(
            "Prepare Python packages",
            preparePythonPackages.ProtocolTool.Title);
        Assert.Contains(
            "official PyPI",
            preparePythonPackages.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "wheels only",
            preparePythonPackages.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "workerRestartRequired",
            preparePythonPackages.Description,
            StringComparison.Ordinal);
        Assert.False(
            preparePythonPackages.ProtocolTool.Annotations?.ReadOnlyHint ?? true);
        Assert.True(
            preparePythonPackages.ProtocolTool.Annotations?.DestructiveHint ?? false);
        Assert.False(
            preparePythonPackages.ProtocolTool.Annotations?.IdempotentHint ?? true);
        Assert.True(
            preparePythonPackages.ProtocolTool.Annotations?.OpenWorldHint ?? false);

        var preparePackageProperties = GetRequiredProperty(
            preparePythonPackages.JsonSchema,
            "properties");
        AssertSchemaRange(
            GetRequiredProperty(
                preparePackageProperties,
                "timeoutSeconds"),
            1,
            600);
        var packagesSchema = GetRequiredProperty(
            preparePackageProperties,
            "packages");
        Assert.Equal(
            32,
            GetRequiredProperty(
                packagesSchema,
                "maxItems").GetInt32());

        var packageItemProperties = GetRequiredProperty(
            GetRequiredProperty(
                GetRequiredProperty(
                    packagesSchema,
                    "items"),
                "properties"),
            "name");
        Assert.Equal(
            128,
            GetRequiredProperty(
                packageItemProperties,
                "maxLength").GetInt32());

        var resetPython = Assert.Single(tools, tool => tool.Name == "python_reset");
        Assert.Equal("Reset Python session", resetPython.ProtocolTool.Title);
        Assert.Contains("not an interrupt", resetPython.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(resetPython.ProtocolTool.Annotations?.DestructiveHint ?? false);
        Assert.True(resetPython.ProtocolTool.Annotations?.IdempotentHint ?? false);
        Assert.False(resetPython.ProtocolTool.Annotations?.OpenWorldHint ?? true);

        var getWorkPlan = Assert.Single(tools, tool => tool.Name == "work_plan_get");
        Assert.Equal("Get work plan", getWorkPlan.ProtocolTool.Title);
        Assert.Contains("resuming or inspecting", getWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("empty plan at revision 0", getWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not call this only to initialize", getWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idle timeout", getWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(getWorkPlan.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.False(getWorkPlan.ProtocolTool.Annotations?.DestructiveHint ?? true);
        Assert.False(getWorkPlan.ProtocolTool.Annotations?.IdempotentHint ?? true);
        Assert.False(getWorkPlan.ProtocolTool.Annotations?.OpenWorldHint ?? true);
        var getPlanProperties = GetRequiredProperty(getWorkPlan.JsonSchema, "properties");
        GetRequiredProperty(getPlanProperties, "workId");
        var getPlanRequired = GetRequiredProperty(getWorkPlan.JsonSchema, "required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("workId", getPlanRequired);

        var updateWorkPlan = Assert.Single(tools, tool => tool.Name == "work_plan_update");
        Assert.Equal("Create or replace work plan", updateWorkPlan.ProtocolTool.Title);
        Assert.Contains("initial creation", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expectedRevision=0", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("work_plan_patch", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reordering", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("complete logical Work Plan", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconcile", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("empty steps array", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(updateWorkPlan.ProtocolTool.Annotations?.ReadOnlyHint ?? true);
        Assert.True(updateWorkPlan.ProtocolTool.Annotations?.DestructiveHint ?? false);
        Assert.False(updateWorkPlan.ProtocolTool.Annotations?.IdempotentHint ?? true);
        Assert.False(updateWorkPlan.ProtocolTool.Annotations?.OpenWorldHint ?? true);

        var updatePlanProperties = GetRequiredProperty(updateWorkPlan.JsonSchema, "properties");
        AssertSchemaRange(
            GetRequiredProperty(updatePlanProperties, "expectedRevision"),
            0,
            long.MaxValue);
        var stepsSchema = GetRequiredProperty(updatePlanProperties, "steps");
        Assert.Equal(32, GetRequiredProperty(stepsSchema, "maxItems").GetInt32());
        Assert.False(stepsSchema.TryGetProperty("minItems", out _));

        var stepProperties = GetRequiredProperty(
            GetRequiredProperty(stepsSchema, "items"),
            "properties");
        var textSchema = GetRequiredProperty(stepProperties, "text");
        Assert.Equal(1, GetRequiredProperty(textSchema, "minLength").GetInt32());
        Assert.Equal(512, GetRequiredProperty(textSchema, "maxLength").GetInt32());
        Assert.Equal(
            "^[^\\r\\n]*$",
            GetRequiredProperty(textSchema, "pattern").GetString());
        AssertSchemaEnum(
            GetRequiredProperty(stepProperties, "status"),
            "pending",
            "active",
            "waiting",
            "completed");

        var updatePlanRequired = GetRequiredProperty(updateWorkPlan.JsonSchema, "required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("workId", updatePlanRequired);
        Assert.Contains("expectedRevision", updatePlanRequired);
        Assert.Contains("steps", updatePlanRequired);

        var patchWorkPlan = Assert.Single(tools, tool => tool.Name == "work_plan_patch");
        Assert.Equal("Patch work plan", patchWorkPlan.ProtocolTool.Title);
        Assert.Contains("without resending untouched steps", patchWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expectedRevision", patchWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not auto-merge", patchWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("complete normalized snapshot", patchWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(patchWorkPlan.ProtocolTool.Annotations?.ReadOnlyHint ?? true);
        Assert.True(patchWorkPlan.ProtocolTool.Annotations?.DestructiveHint ?? false);
        Assert.False(patchWorkPlan.ProtocolTool.Annotations?.IdempotentHint ?? true);
        Assert.False(patchWorkPlan.ProtocolTool.Annotations?.OpenWorldHint ?? true);

        var patchPlanProperties = GetRequiredProperty(patchWorkPlan.JsonSchema, "properties");
        AssertSchemaRange(
            GetRequiredProperty(patchPlanProperties, "expectedRevision"),
            0,
            long.MaxValue);
        var changesSchema = GetRequiredProperty(patchPlanProperties, "changes");
        Assert.Equal(1, GetRequiredProperty(changesSchema, "minItems").GetInt32());
        Assert.Equal(32, GetRequiredProperty(changesSchema, "maxItems").GetInt32());

        var patchChangeProperties = GetRequiredProperty(
            GetRequiredProperty(changesSchema, "items"),
            "properties");
        AssertSchemaEnum(
            GetRequiredProperty(patchChangeProperties, "op"),
            "add",
            "update",
            "remove");
        AssertSchemaEnum(
            GetRequiredProperty(patchChangeProperties, "status"),
            "pending",
            "active",
            "waiting",
            "completed");

        var changeRequired = GetRequiredProperty(
                GetRequiredProperty(changesSchema, "items"),
                "required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("op", changeRequired);
        Assert.DoesNotContain("id", changeRequired);
        Assert.DoesNotContain("text", changeRequired);
        Assert.DoesNotContain("status", changeRequired);

        var patchPlanRequired = GetRequiredProperty(patchWorkPlan.JsonSchema, "required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("workId", patchPlanRequired);
        Assert.Contains("expectedRevision", patchPlanRequired);
        Assert.Contains("changes", patchPlanRequired);
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
        Assert.Contains("process_run", toolNames);
        Assert.Contains("process_status", toolNames);
        Assert.Contains("process_read", toolNames);
        Assert.Contains("process_write", toolNames);
        Assert.Contains("process_resize", toolNames);
        Assert.Contains("process_terminate", toolNames);
        Assert.Contains("process_release", toolNames);
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

        var release = await client.CallToolAsync(
            "process_release",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle
            });
        var releaseRoot = GetStructured(release.StructuredContent);
        Assert.True(GetRequiredProperty(releaseRoot, "ok").GetBoolean());
        Assert.True(GetRequiredProperty(releaseRoot, "result").GetBoolean());

        var releasedStatus = await client.CallToolAsync(
            "process_status",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle
            });
        var releasedStatusRoot =
            GetStructured(releasedStatus.StructuredContent);
        Assert.False(
            GetRequiredProperty(releasedStatusRoot, "ok").GetBoolean());
        Assert.Equal(
            "resource_closed",
            GetRequiredProperty(
                GetRequiredProperty(releasedStatusRoot, "error"),
                "code").GetString());

        var secondRelease = await client.CallToolAsync(
            "process_release",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle
            });
        Assert.True(
            GetRequiredProperty(
                GetStructured(secondRelease.StructuredContent),
                "ok").GetBoolean());

        var close = await client.CallToolAsync(
            "work_close",
            new Dictionary<string, object?> { ["workId"] = workId });

        var closeRoot = GetStructured(close.StructuredContent);
        Assert.True(GetRequiredProperty(closeRoot, "ok").GetBoolean());
    }


    [Fact]
    public async Task StdioAdapterCanRunShortProcessInOneCall()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI process_run integration test",
            Command = "dotnet",
            Arguments = [hostDll],
            WorkingDirectory = repoRoot,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });

        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "process_run",
            new Dictionary<string, object?>
            {
                ["executable"] = "cmd.exe",
                ["arguments"] = new[]
                {
                    "/d",
                    "/s",
                    "/c",
                    "echo mcp-run-out & echo mcp-run-err 1>&2 & exit /b 7"
                },
                ["timeoutSeconds"] = 5,
                ["maxOutputChars"] = 4096
            });

        var root = GetStructured(result.StructuredContent);
        Assert.True(GetRequiredProperty(root, "ok").GetBoolean());
        var payload = GetRequiredProperty(root, "result");
        Assert.Equal(7, GetRequiredProperty(payload, "exitCode").GetInt32());
        Assert.Contains(
            "mcp-run-out",
            GetRequiredProperty(payload, "stdout").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "mcp-run-err",
            GetRequiredProperty(payload, "stderr").GetString(),
            StringComparison.Ordinal);
        Assert.False(
            GetRequiredProperty(payload, "stdoutTruncated").GetBoolean());
        Assert.False(
            GetRequiredProperty(payload, "stderrTruncated").GetBoolean());
    }

    [Fact]
    public async Task StdioAdapterCanTerminateRunningProcess()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI terminate integration test",
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
                ["label"] = "terminate-integration-test"
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
                    "Write-Output 'TERMINATE-READY'; Start-Sleep -Seconds 60"
                },
                ["workId"] = workId
            });

        var startRoot = GetStructured(start.StructuredContent);
        Assert.True(GetRequiredProperty(startRoot, "ok").GetBoolean());
        var processHandle = GetRequiredProperty(
            GetRequiredProperty(startRoot, "result"),
            "processHandle").GetString();
        Assert.False(string.IsNullOrWhiteSpace(processHandle));

        var terminate = await client.CallToolAsync(
            "process_terminate",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle
            });
        Assert.True(
            GetRequiredProperty(
                GetStructured(terminate.StructuredContent),
                "ok").GetBoolean());

        JsonElement statusResult = default;
        for (var i = 0; i < 200; i++)
        {
            var status = await client.CallToolAsync(
                "process_status",
                new Dictionary<string, object?>
                {
                    ["processHandle"] = processHandle
                });
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

        Assert.Equal(
            "terminated",
            GetRequiredProperty(statusResult, "state").GetString());

        var release = await client.CallToolAsync(
            "process_release",
            new Dictionary<string, object?>
            {
                ["processHandle"] = processHandle
            });
        Assert.True(
            GetRequiredProperty(
                GetStructured(release.StructuredContent),
                "ok").GetBoolean());

        var close = await client.CallToolAsync(
            "work_close",
            new Dictionary<string, object?> { ["workId"] = workId });
        Assert.True(
            GetRequiredProperty(
                GetStructured(close.StructuredContent),
                "ok").GetBoolean());
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

    [Fact]
    public async Task StdioAdapterRejectsReadFilesResponsesAboveMcpBudget()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var scratch = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.ReadFilesPayloadIntegration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        const int maxBudgetBytes = 9 * 1024 * 1024;
        const int escapedTextBytes = 5 * 1024 * 1024;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(scratch, "raw-over-budget.txt"),
                new string('x', maxBudgetBytes + 1),
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(scratch, "escaped-over-budget.txt"),
                new string('\\', escapedTextBytes),
                new UTF8Encoding(false));

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "LoomLCI read_files payload integration test",
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
                    ["label"] = "read-files-payload-integration-test"
                });
            var workId = GetRequiredProperty(
                GetRequiredProperty(GetStructured(create.StructuredContent), "result"),
                "workId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));

            async Task<CallToolResult> ReadAsync(string path)
                => await client.CallToolAsync(
                    "filesystem_read_files",
                    new Dictionary<string, object?>
                    {
                        ["files"] = new[]
                        {
                            new Dictionary<string, object?> { ["path"] = path }
                        },
                        ["workId"] = workId
                    });

            var rawOversized = await ReadAsync("raw-over-budget.txt");
            Assert.True(rawOversized.IsError);
            var rawError = GetRequiredProperty(
                GetStructured(rawOversized.StructuredContent),
                "error");
            var rawDetails = GetRequiredProperty(rawError, "details");
            Assert.Equal(
                "unsupported",
                GetRequiredProperty(rawError, "code").GetString());
            Assert.Equal(
                "mcp_payload_too_large",
                GetRequiredProperty(rawDetails, "reason").GetString());
            Assert.Equal(
                maxBudgetBytes + 1L,
                GetRequiredProperty(rawDetails, "returnedTextUtf8Bytes").GetInt64());
            Assert.Equal(
                maxBudgetBytes,
                GetRequiredProperty(rawDetails, "maxCallToolResultBytes").GetInt32());
            Assert.False(rawDetails.TryGetProperty(
                "serializedCallToolResultBytes",
                out _));

            var escapedOversized = await ReadAsync("escaped-over-budget.txt");
            Assert.True(escapedOversized.IsError);
            var escapedError = GetRequiredProperty(
                GetStructured(escapedOversized.StructuredContent),
                "error");
            var escapedDetails = GetRequiredProperty(escapedError, "details");
            Assert.Equal(
                "mcp_payload_too_large",
                GetRequiredProperty(escapedDetails, "reason").GetString());
            Assert.Equal(
                escapedTextBytes,
                GetRequiredProperty(escapedDetails, "returnedTextUtf8Bytes").GetInt64());
            Assert.True(
                GetRequiredProperty(
                    escapedDetails,
                    "serializedCallToolResultBytes").GetInt64() >
                maxBudgetBytes);
            Assert.Equal(
                maxBudgetBytes,
                GetRequiredProperty(escapedDetails, "maxCallToolResultBytes").GetInt32());

            var close = await client.CallToolAsync(
                "work_close",
                new Dictionary<string, object?> { ["workId"] = workId });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(close.StructuredContent),
                    "ok").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StdioAdapterCanViewLocalImage()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var scratch = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.VisualIntegration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            var imageBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPkqrjDwMDAxMDAwMDAAAAPwAFizZEe6AAAAABJRU5ErkJggg==");
            await File.WriteAllBytesAsync(
                Path.Combine(scratch, "sample.bin"),
                imageBytes);
            await File.WriteAllBytesAsync(
                Path.Combine(scratch, "fake.png"),
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "LoomLCI visual image integration test",
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
                    ["label"] = "visual-image-integration-test"
                });
            var createRoot = GetStructured(create.StructuredContent);
            var workId = GetRequiredProperty(
                GetRequiredProperty(createRoot, "result"),
                "workId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));

            var viewed = await client.CallToolAsync(
                "filesystem_view_image",
                new Dictionary<string, object?>
                {
                    ["path"] = "sample.bin",
                    ["workId"] = workId
                });

            Assert.False(viewed.IsError ?? false);
            Assert.Collection(
                viewed.Content,
                block => Assert.Equal(
                    "Tool completed successfully. Structured result attached.",
                    Assert.IsType<TextContentBlock>(block).Text),
                block =>
                {
                    var image = Assert.IsType<ImageContentBlock>(block);
                    Assert.Equal("image/png", image.MimeType);
                    Assert.True(image.DecodedData.Span.SequenceEqual(imageBytes));
                });

            var viewedRoot = GetStructured(viewed.StructuredContent);
            Assert.True(GetRequiredProperty(viewedRoot, "ok").GetBoolean());
            var viewedResult = GetRequiredProperty(viewedRoot, "result");
            Assert.Equal("sample.bin", GetRequiredProperty(viewedResult, "requestedPath").GetString());
            Assert.Equal(
                Path.Combine(scratch, "sample.bin"),
                GetRequiredProperty(viewedResult, "fullPath").GetString());
            Assert.Equal("image/png", GetRequiredProperty(viewedResult, "mimeType").GetString());
            Assert.Equal(imageBytes.Length, GetRequiredProperty(viewedResult, "sizeBytes").GetInt64());

            var invalid = await client.CallToolAsync(
                "filesystem_view_image",
                new Dictionary<string, object?>
                {
                    ["path"] = "fake.png",
                    ["workId"] = workId
                });

            Assert.True(invalid.IsError);
            Assert.DoesNotContain(invalid.Content, block => block is ImageContentBlock);
            var invalidError = GetRequiredProperty(
                GetStructured(invalid.StructuredContent),
                "error");
            Assert.Equal(
                "unsupported",
                GetRequiredProperty(invalidError, "code").GetString());
            Assert.Equal(
                "unsupported_image_format",
                GetRequiredProperty(
                    GetRequiredProperty(invalidError, "details"),
                    "reason").GetString());

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

    [Fact]
    public async Task StdioAdapterCanReadPdfTextThroughIsolatedWorker()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var scratch = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.PdfIntegration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            var pdfPath = Path.Combine(scratch, "sample.pdf");
            CreateSimplePdf(pdfPath, "Page one alpha", "Page two beta", "Page three gamma");
            await File.WriteAllTextAsync(
                Path.Combine(scratch, "invalid.pdf"),
                "%PDF-1.4\nthis is not a valid PDF");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "LoomLCI PDF integration test",
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
                    ["label"] = "pdf-integration-test"
                });
            var workId = GetRequiredProperty(
                GetRequiredProperty(GetStructured(create.StructuredContent), "result"),
                "workId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));

            var read = await client.CallToolAsync(
                "filesystem_read_pdf",
                new Dictionary<string, object?>
                {
                    ["path"] = "sample.pdf",
                    ["workId"] = workId,
                    ["startPage"] = 2,
                    ["maxPages"] = 1
                });

            Assert.False(read.IsError ?? false);
            var root = GetStructured(read.StructuredContent);
            Assert.True(GetRequiredProperty(root, "ok").GetBoolean());
            var result = GetRequiredProperty(root, "result");
            Assert.Equal("sample.pdf", GetRequiredProperty(result, "requestedPath").GetString());
            Assert.Equal(3, GetRequiredProperty(result, "pageCount").GetInt32());
            Assert.Equal(2, GetRequiredProperty(result, "startPage").GetInt32());
            Assert.Equal(2, GetRequiredProperty(result, "endPage").GetInt32());
            Assert.True(GetRequiredProperty(result, "hasMoreAfter").GetBoolean());
            Assert.Equal(3, GetRequiredProperty(result, "nextPage").GetInt32());
            var pages = GetRequiredProperty(result, "pages").EnumerateArray().ToArray();
            var page = Assert.Single(pages);
            Assert.Equal(2, GetRequiredProperty(page, "pageNumber").GetInt32());
            Assert.Contains("Page two beta", GetRequiredProperty(page, "text").GetString());
            Assert.False(GetRequiredProperty(page, "textTruncated").GetBoolean());

            var outOfRange = await client.CallToolAsync(
                "filesystem_read_pdf",
                new Dictionary<string, object?>
                {
                    ["path"] = "sample.pdf",
                    ["workId"] = workId,
                    ["startPage"] = 99
                });
            Assert.True(outOfRange.IsError);
            var outOfRangeError = GetRequiredProperty(
                GetStructured(outOfRange.StructuredContent),
                "error");
            Assert.Equal("invalid_argument", GetRequiredProperty(outOfRangeError, "code").GetString());
            Assert.Equal(
                "page_out_of_range",
                GetRequiredProperty(
                    GetRequiredProperty(outOfRangeError, "details"),
                    "reason").GetString());

            var invalid = await client.CallToolAsync(
                "filesystem_read_pdf",
                new Dictionary<string, object?>
                {
                    ["path"] = "invalid.pdf",
                    ["workId"] = workId
                });
            Assert.True(invalid.IsError);
            var invalidError = GetRequiredProperty(
                GetStructured(invalid.StructuredContent),
                "error");
            Assert.Equal("unsupported", GetRequiredProperty(invalidError, "code").GetString());
            Assert.Equal(
                "invalid_pdf",
                GetRequiredProperty(
                    GetRequiredProperty(invalidError, "details"),
                    "reason").GetString());

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

    [Fact]
    public async Task StdioAdapterCanRenderPdfPageThroughIsolatedWorker()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var scratch = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.PdfRenderIntegration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            CreateSimplePdf(Path.Combine(scratch, "visual.pdf"), "Rendered page alpha");
            await File.WriteAllTextAsync(
                Path.Combine(scratch, "invalid.pdf"),
                "%PDF-1.4\nthis is not a valid PDF");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "LoomLCI PDF render integration test",
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
                    ["label"] = "pdf-render-integration-test"
                });
            var workId = GetRequiredProperty(
                GetRequiredProperty(GetStructured(create.StructuredContent), "result"),
                "workId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));

            var rendered = await client.CallToolAsync(
                "filesystem_render_pdf_page",
                new Dictionary<string, object?>
                {
                    ["path"] = "visual.pdf",
                    ["workId"] = workId,
                    ["page"] = 1,
                    ["maxWidth"] = 1200,
                    ["maxHeight"] = 1200
                });

            Assert.False(rendered.IsError ?? false);
            var renderedRoot = GetStructured(rendered.StructuredContent);
            Assert.True(GetRequiredProperty(renderedRoot, "ok").GetBoolean());
            var renderedResult = GetRequiredProperty(renderedRoot, "result");
            Assert.Equal("visual.pdf", GetRequiredProperty(renderedResult, "requestedPath").GetString());
            Assert.Equal(1, GetRequiredProperty(renderedResult, "page").GetInt32());
            Assert.Equal(1, GetRequiredProperty(renderedResult, "pageCount").GetInt32());
            Assert.Equal("image/png", GetRequiredProperty(renderedResult, "mimeType").GetString());

            var width = GetRequiredProperty(renderedResult, "width").GetInt32();
            var height = GetRequiredProperty(renderedResult, "height").GetInt32();
            Assert.InRange(width, 1, 1200);
            Assert.InRange(height, 1, 1200);

            Assert.Collection(
                rendered.Content,
                block => Assert.IsType<TextContentBlock>(block),
                block =>
                {
                    var image = Assert.IsType<ImageContentBlock>(block);
                    Assert.Equal("image/png", image.MimeType);
                    var png = image.DecodedData.Span;
                    Assert.True(png.Length > 24);
                    Assert.True(png[..8].SequenceEqual(
                        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
                    Assert.Equal(
                        width,
                        checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4))));
                    Assert.Equal(
                        height,
                        checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4))));
                    Assert.Equal(
                        png.Length,
                        GetRequiredProperty(renderedResult, "imageSizeBytes").GetInt64());
                });

            var outOfRange = await client.CallToolAsync(
                "filesystem_render_pdf_page",
                new Dictionary<string, object?>
                {
                    ["path"] = "visual.pdf",
                    ["workId"] = workId,
                    ["page"] = 2
                });
            Assert.True(outOfRange.IsError);
            var outOfRangeError = GetRequiredProperty(
                GetStructured(outOfRange.StructuredContent),
                "error");
            Assert.Equal("invalid_argument", GetRequiredProperty(outOfRangeError, "code").GetString());
            Assert.Equal(
                "page_out_of_range",
                GetRequiredProperty(
                    GetRequiredProperty(outOfRangeError, "details"),
                    "reason").GetString());

            var invalid = await client.CallToolAsync(
                "filesystem_render_pdf_page",
                new Dictionary<string, object?>
                {
                    ["path"] = "invalid.pdf",
                    ["workId"] = workId
                });
            Assert.True(invalid.IsError);
            Assert.DoesNotContain(invalid.Content, block => block is ImageContentBlock);
            var invalidError = GetRequiredProperty(
                GetStructured(invalid.StructuredContent),
                "error");
            Assert.Equal("unsupported", GetRequiredProperty(invalidError, "code").GetString());
            Assert.Equal(
                "invalid_pdf",
                GetRequiredProperty(
                    GetRequiredProperty(invalidError, "details"),
                    "reason").GetString());

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

    [Fact]
    public async Task StdioAdapterCanRoundTripWorkPlanAndRecoverFromConflict()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI Work Plan integration test",
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
                ["label"] = "work-plan-integration-test"
            });
        var createRoot = GetStructured(create.StructuredContent);
        Assert.True(GetRequiredProperty(createRoot, "ok").GetBoolean());
        var workId = GetRequiredProperty(
            GetRequiredProperty(createRoot, "result"),
            "workId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workId));

        var initial = await client.CallToolAsync(
            "work_plan_get",
            new Dictionary<string, object?> { ["workId"] = workId });
        Assert.Equal(
            "Tool completed successfully. Structured result attached.",
            GetSingleTextContent(initial));
        var initialRoot = GetStructured(initial.StructuredContent);
        Assert.True(GetRequiredProperty(initialRoot, "ok").GetBoolean());
        var initialResult = GetRequiredProperty(initialRoot, "result");
        Assert.Equal(0, GetRequiredProperty(initialResult, "revision").GetInt64());
        Assert.Empty(GetRequiredProperty(initialResult, "steps").EnumerateArray());

        var firstUpdate = await client.CallToolAsync(
            "work_plan_update",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 0L,
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["text"] = "Compile project",
                        ["status"] = "waiting"
                    },
                    new Dictionary<string, object?>
                    {
                        ["text"] = "Update documentation",
                        ["status"] = "active"
                    }
                }
            });
        var firstRoot = GetStructured(firstUpdate.StructuredContent);
        Assert.True(GetRequiredProperty(firstRoot, "ok").GetBoolean());
        var firstResult = GetRequiredProperty(firstRoot, "result");
        Assert.Equal(1, GetRequiredProperty(firstResult, "revision").GetInt64());
        var firstSteps = GetRequiredProperty(firstResult, "steps")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, firstSteps.Length);

        var compileId = GetRequiredProperty(firstSteps[0], "id").GetString();
        var docsId = GetRequiredProperty(firstSteps[1], "id").GetString();
        Assert.StartsWith("step_", compileId, StringComparison.Ordinal);
        Assert.StartsWith("step_", docsId, StringComparison.Ordinal);
        Assert.Equal("waiting", GetRequiredProperty(firstSteps[0], "status").GetString());
        Assert.Equal("active", GetRequiredProperty(firstSteps[1], "status").GetString());

        var observed = await client.CallToolAsync(
            "work_plan_get",
            new Dictionary<string, object?> { ["workId"] = workId });
        var observedResult = GetRequiredProperty(
            GetStructured(observed.StructuredContent),
            "result");
        Assert.Equal(1, GetRequiredProperty(observedResult, "revision").GetInt64());
        Assert.Equal(
            new[] { compileId, docsId },
            GetRequiredProperty(observedResult, "steps")
                .EnumerateArray()
                .Select(step => GetRequiredProperty(step, "id").GetString())
                .ToArray());

        var stale = await client.CallToolAsync(
            "work_plan_update",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 0L,
                ["steps"] = Array.Empty<object>()
            });
        Assert.True(stale.IsError is true);
        var staleRoot = GetStructured(stale.StructuredContent);
        Assert.False(GetRequiredProperty(staleRoot, "ok").GetBoolean());
        var staleError = GetRequiredProperty(staleRoot, "error");
        Assert.Equal("conflict", GetRequiredProperty(staleError, "code").GetString());
        Assert.Equal(
            1,
            GetRequiredProperty(
                GetRequiredProperty(staleError, "details"),
                "currentRevision").GetInt64());

        var reconciled = await client.CallToolAsync(
            "work_plan_update",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 1L,
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = docsId,
                        ["text"] = "Update documentation",
                        ["status"] = "completed"
                    },
                    new Dictionary<string, object?>
                    {
                        ["id"] = compileId,
                        ["text"] = "Compile project",
                        ["status"] = "active"
                    }
                }
            });
        var reconciledResult = GetRequiredProperty(
            GetStructured(reconciled.StructuredContent),
            "result");
        Assert.Equal(2, GetRequiredProperty(reconciledResult, "revision").GetInt64());
        var reconciledSteps = GetRequiredProperty(reconciledResult, "steps")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(docsId, GetRequiredProperty(reconciledSteps[0], "id").GetString());
        Assert.Equal("completed", GetRequiredProperty(reconciledSteps[0], "status").GetString());
        Assert.Equal(compileId, GetRequiredProperty(reconciledSteps[1], "id").GetString());
        Assert.Equal("active", GetRequiredProperty(reconciledSteps[1], "status").GetString());

        var invalidStatus = await client.CallToolAsync(
            "work_plan_update",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 2L,
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = compileId,
                        ["text"] = "Compile project",
                        ["status"] = "not-a-status"
                    }
                }
            });
        Assert.True(invalidStatus.IsError is true);
        Assert.Equal(
            "invalid_argument",
            GetRequiredProperty(
                GetRequiredProperty(
                    GetStructured(invalidStatus.StructuredContent),
                    "error"),
                "code").GetString());

        var cleared = await client.CallToolAsync(
            "work_plan_update",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 2L,
                ["steps"] = Array.Empty<object>()
            });
        var clearedResult = GetRequiredProperty(
            GetStructured(cleared.StructuredContent),
            "result");
        Assert.Equal(3, GetRequiredProperty(clearedResult, "revision").GetInt64());
        Assert.Empty(GetRequiredProperty(clearedResult, "steps").EnumerateArray());

        var close = await client.CallToolAsync(
            "work_close",
            new Dictionary<string, object?> { ["workId"] = workId });
        Assert.True(
            GetRequiredProperty(
                GetStructured(close.StructuredContent),
                "ok").GetBoolean());

        var afterClose = await client.CallToolAsync(
            "work_plan_get",
            new Dictionary<string, object?> { ["workId"] = workId });
        Assert.True(afterClose.IsError is true);
        Assert.Equal(
            "resource_closed",
            GetRequiredProperty(
                GetRequiredProperty(
                    GetStructured(afterClose.StructuredContent),
                    "error"),
                "code").GetString());
    }

    [Fact]
    public async Task StdioAdapterCanPatchWorkPlanAtomicallyAndPreserveIdentity()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "LoomLCI Work Plan patch integration test",
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
                ["label"] = "work-plan-patch-integration-test"
            });
        var createRoot = GetStructured(create.StructuredContent);
        var workId = GetRequiredProperty(
            GetRequiredProperty(createRoot, "result"),
            "workId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workId));

        var initial = await client.CallToolAsync(
            "work_plan_update",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 0L,
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["text"] = "Keep identity",
                        ["status"] = "active"
                    },
                    new Dictionary<string, object?>
                    {
                        ["text"] = "Remove me",
                        ["status"] = "waiting"
                    }
                }
            });

        var initialResult = GetRequiredProperty(
            GetStructured(initial.StructuredContent),
            "result");
        var initialSteps = GetRequiredProperty(initialResult, "steps")
            .EnumerateArray()
            .ToArray();
        var keepId = GetRequiredProperty(initialSteps[0], "id").GetString();
        var removeId = GetRequiredProperty(initialSteps[1], "id").GetString();

        var patched = await client.CallToolAsync(
            "work_plan_patch",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 1L,
                ["changes"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "update",
                        ["id"] = keepId,
                        ["status"] = "completed"
                    },
                    new Dictionary<string, object?>
                    {
                        ["op"] = "remove",
                        ["id"] = removeId
                    },
                    new Dictionary<string, object?>
                    {
                        ["op"] = "add",
                        ["text"] = "Added by patch"
                    }
                }
            });

        var patchedRoot = GetStructured(patched.StructuredContent);
        Assert.True(GetRequiredProperty(patchedRoot, "ok").GetBoolean());
        var patchedResult = GetRequiredProperty(patchedRoot, "result");
        Assert.Equal(2, GetRequiredProperty(patchedResult, "revision").GetInt64());
        var patchedSteps = GetRequiredProperty(patchedResult, "steps")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, patchedSteps.Length);

        Assert.Equal(keepId, GetRequiredProperty(patchedSteps[0], "id").GetString());
        Assert.Equal("Keep identity", GetRequiredProperty(patchedSteps[0], "text").GetString());
        Assert.Equal("completed", GetRequiredProperty(patchedSteps[0], "status").GetString());

        var addedId = GetRequiredProperty(patchedSteps[1], "id").GetString();
        Assert.StartsWith("step_", addedId, StringComparison.Ordinal);
        Assert.NotEqual(keepId, addedId);
        Assert.NotEqual(removeId, addedId);
        Assert.Equal("Added by patch", GetRequiredProperty(patchedSteps[1], "text").GetString());
        Assert.Equal("pending", GetRequiredProperty(patchedSteps[1], "status").GetString());

        var stale = await client.CallToolAsync(
            "work_plan_patch",
            new Dictionary<string, object?>
            {
                ["workId"] = workId,
                ["expectedRevision"] = 1L,
                ["changes"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "remove",
                        ["id"] = "step_unknown"
                    }
                }
            });

        Assert.True(stale.IsError is true);
        var staleError = GetRequiredProperty(
            GetStructured(stale.StructuredContent),
            "error");
        Assert.Equal("conflict", GetRequiredProperty(staleError, "code").GetString());
        Assert.Equal(
            2,
            GetRequiredProperty(
                GetRequiredProperty(staleError, "details"),
                "currentRevision").GetInt64());

        var close = await client.CallToolAsync(
            "work_close",
            new Dictionary<string, object?> { ["workId"] = workId });
        Assert.True(
            GetRequiredProperty(
                GetStructured(close.StructuredContent),
                "ok").GetBoolean());
    }

    [Fact]
    public async Task StdioAdapterCanExecutePersistentPythonAndResetIt()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var hostWorkingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostWorkingDirectory);

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "LoomLCI Python integration test",
                Command = "dotnet",
                Arguments = [hostDll],
                WorkingDirectory = hostWorkingDirectory,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });

            await using var client = await McpClient.CreateAsync(transport);

            var create = await client.CallToolAsync(
                "work_create",
                new Dictionary<string, object?>
                {
                    ["baseDirectory"] = repoRoot,
                    ["label"] = "python-integration-test"
                });

            var createRoot = GetStructured(create.StructuredContent);
            Assert.True(GetRequiredProperty(createRoot, "ok").GetBoolean());
            var workId = GetRequiredProperty(
                GetRequiredProperty(createRoot, "result"),
                "workId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));

            var assign = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "x = 40"
                });

            var assignRoot = GetStructured(assign.StructuredContent);
            Assert.True(GetRequiredProperty(assignRoot, "ok").GetBoolean());
            Assert.Equal(
                "completed",
                GetRequiredProperty(
                    GetRequiredProperty(assignRoot, "result"),
                    "status").GetString());

            var bridge = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] =
                        "import loom\n" +
                        "caps = loom.capabilities()\n" +
                        "print(loom.__bridge_version__)\n" +
                        "print(len(caps))\n" +
                        "print(caps[0])\n" +
                        "print(caps[-1])"
                });

            var bridgeRoot = GetStructured(bridge.StructuredContent);
            Assert.True(GetRequiredProperty(bridgeRoot, "ok").GetBoolean());
            var bridgeResult = GetRequiredProperty(
                bridgeRoot,
                "result");
            Assert.Equal(
                "completed",
                GetRequiredProperty(
                    bridgeResult,
                    "status").GetString());
            Assert.Equal(
                "2\n16\nfs.apply_patch\nprocess.write\n",
                GetRequiredProperty(
                    bridgeResult,
                    "stdout").GetString());

            var print = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print(x + 2)"
                });

            Assert.Equal(
                "Tool completed successfully. Structured result attached.",
                GetSingleTextContent(print));
            Assert.DoesNotContain("42", GetSingleTextContent(print), StringComparison.Ordinal);

            var printRoot = GetStructured(print.StructuredContent);
            var printResult = GetRequiredProperty(printRoot, "result");
            Assert.True(GetRequiredProperty(printRoot, "ok").GetBoolean());
            Assert.Equal("42\n", GetRequiredProperty(printResult, "stdout").GetString());
            Assert.Equal("", GetRequiredProperty(printResult, "stderr").GetString());
            Assert.False(GetRequiredProperty(printResult, "stdoutTruncated").GetBoolean());

            var exception = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "raise ValueError('boom')"
                });

            Assert.False(exception.IsError ?? false);
            Assert.DoesNotContain("boom", GetSingleTextContent(exception), StringComparison.Ordinal);
            Assert.DoesNotContain("Traceback", GetSingleTextContent(exception), StringComparison.Ordinal);

            var exceptionRoot = GetStructured(exception.StructuredContent);
            Assert.True(GetRequiredProperty(exceptionRoot, "ok").GetBoolean());
            var exceptionResult = GetRequiredProperty(exceptionRoot, "result");
            Assert.Equal("exception", GetRequiredProperty(exceptionResult, "status").GetString());
            var exceptionInfo = GetRequiredProperty(exceptionResult, "exception");
            Assert.Equal("ValueError", GetRequiredProperty(exceptionInfo, "type").GetString());
            Assert.Equal("boom", GetRequiredProperty(exceptionInfo, "message").GetString());
            Assert.Contains(
                "ValueError: boom",
                GetRequiredProperty(exceptionInfo, "traceback").GetString(),
                StringComparison.Ordinal);

            var unicode = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print('😀😀😀')",
                    ["maxOutputChars"] = 3
                });

            var unicodeResult = GetRequiredProperty(
                GetStructured(unicode.StructuredContent),
                "result");
            Assert.Equal("😀😀😀", GetRequiredProperty(unicodeResult, "stdout").GetString());
            Assert.True(GetRequiredProperty(unicodeResult, "stdoutTruncated").GetBoolean());

            var oversizedCode = new string('x', 256 * 1024 + 1);
            var oversized = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = oversizedCode
                });

            Assert.True(oversized.IsError is true);
            var oversizedRoot = GetStructured(oversized.StructuredContent);
            Assert.False(GetRequiredProperty(oversizedRoot, "ok").GetBoolean());
            Assert.Equal(
                "invalid_argument",
                GetRequiredProperty(
                    GetRequiredProperty(oversizedRoot, "error"),
                    "code").GetString());

            var afterOversized = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print(x)"
                });
            Assert.Equal(
                "40\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(afterOversized.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var reset = await client.CallToolAsync(
                "python_reset",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId
                });
            var resetRoot = GetStructured(reset.StructuredContent);
            Assert.True(GetRequiredProperty(resetRoot, "ok").GetBoolean());
            Assert.True(GetRequiredProperty(resetRoot, "result").GetBoolean());

            var afterReset = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print('x' in globals())"
                });
            Assert.Equal(
                "False\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(afterReset.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var timeout = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "while True:\n    pass",
                    ["timeoutSeconds"] = 1
                });
            Assert.True(timeout.IsError is true);
            Assert.Equal(
                "deadline_exceeded",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(timeout.StructuredContent),
                        "error"),
                    "code").GetString());

            var recovered = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print('fresh')"
                });
            Assert.Equal(
                "fresh\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(recovered.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var close = await client.CallToolAsync(
                "work_close",
                new Dictionary<string, object?> { ["workId"] = workId });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(close.StructuredContent),
                    "ok").GetBoolean());

            var afterClose = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print('closed')"
                });
            Assert.True(afterClose.IsError is true);
            Assert.Equal(
                "resource_closed",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(afterClose.StructuredContent),
                        "error"),
                    "code").GetString());
        }
        finally
        {
            try
            {
                Directory.Delete(hostWorkingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task StdioAdapterCanReturnPythonImagesAndHardenFinalPayload()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var hostWorkingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-image-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostWorkingDirectory);

        const string pngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPkqrjDwMDAxMDAwMDAAAAPwAFizZEe6AAAAABJRU5ErkJggg==";
        var expectedPng = Convert.FromBase64String(pngBase64);

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "LoomLCI Python image integration test",
                Command = "dotnet",
                Arguments = [hostDll],
                WorkingDirectory = hostWorkingDirectory,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });

            await using var client = await McpClient.CreateAsync(transport);

            var create = await client.CallToolAsync(
                "work_create",
                new Dictionary<string, object?>
                {
                    ["baseDirectory"] = repoRoot,
                    ["label"] = "python-image-integration-test"
                });
            var workId = GetRequiredProperty(
                GetRequiredProperty(
                    GetStructured(create.StructuredContent),
                    "result"),
                "workId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workId));

            var multiple = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] =
                        "import base64, loom\n" +
                        $"img = base64.b64decode('{pngBase64}')\n" +
                        "loom.display_image(img)\n" +
                        "loom.display_image(bytearray(img))\n" +
                        "print('two-images')"
                });

            Assert.False(multiple.IsError ?? false);
            Assert.Collection(
                multiple.Content,
                block => Assert.IsType<TextContentBlock>(block),
                block =>
                {
                    var image = Assert.IsType<ImageContentBlock>(block);
                    Assert.Equal("image/png", image.MimeType);
                    Assert.True(image.DecodedData.Span.SequenceEqual(expectedPng));
                },
                block =>
                {
                    var image = Assert.IsType<ImageContentBlock>(block);
                    Assert.Equal("image/png", image.MimeType);
                    Assert.True(image.DecodedData.Span.SequenceEqual(expectedPng));
                });

            var multipleRoot = GetStructured(multiple.StructuredContent);
            Assert.True(GetRequiredProperty(multipleRoot, "ok").GetBoolean());
            var multipleResult = GetRequiredProperty(multipleRoot, "result");
            Assert.Equal(
                "two-images\n",
                GetRequiredProperty(multipleResult, "stdout").GetString());
            var outputs = GetRequiredProperty(multipleResult, "outputs")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(2, outputs.Length);
            Assert.All(outputs, output =>
            {
                Assert.Equal(
                    "image",
                    GetRequiredProperty(output, "kind").GetString());
                Assert.Equal(
                    "image/png",
                    GetRequiredProperty(output, "mimeType").GetString());
                Assert.Equal(
                    expectedPng.LongLength,
                    GetRequiredProperty(output, "sizeBytes").GetInt64());
                Assert.False(output.TryGetProperty("data", out _));
            });

            var exception = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] =
                        "import loom\n" +
                        "loom.display_image(img)\n" +
                        "exception_marker = 1234\n" +
                        "raise RuntimeError('after-image')"
                });

            Assert.False(exception.IsError ?? false);
            Assert.Contains(exception.Content, block => block is ImageContentBlock);
            var exceptionResult = GetRequiredProperty(
                GetRequiredProperty(
                    GetStructured(exception.StructuredContent),
                    "result"),
                "status").GetString();
            Assert.Equal("exception", exceptionResult);

            var invalid = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] =
                        "import loom\n" +
                        "invalid_marker = 777\n" +
                        "loom.display_image(img)\n" +
                        "loom.display_image(b'not-an-image')"
                });

            Assert.True(invalid.IsError);
            Assert.DoesNotContain(invalid.Content, block => block is ImageContentBlock);
            var invalidError = GetRequiredProperty(
                GetStructured(invalid.StructuredContent),
                "error");
            Assert.Equal(
                "unsupported",
                GetRequiredProperty(invalidError, "code").GetString());
            Assert.False(
                GetRequiredProperty(invalidError, "retryable").GetBoolean());
            var invalidDetails = GetRequiredProperty(invalidError, "details");
            Assert.Equal(
                "unsupported_image_format",
                GetRequiredProperty(invalidDetails, "reason").GetString());
            Assert.True(
                GetRequiredProperty(
                    invalidDetails,
                    "executionCompleted").GetBoolean());
            Assert.Equal(
                1,
                GetRequiredProperty(invalidDetails, "outputIndex").GetInt32());

            var afterInvalid = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print(invalid_marker)"
                });
            Assert.Equal(
                "777\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(afterInvalid.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var oversizedText = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] =
                        "import sys\n" +
                        "payload_marker = 888\n" +
                        "print('é' * 900000)\n" +
                        "print('é' * 900000, file=sys.stderr)",
                    ["maxOutputChars"] = 900000
                });

            Assert.True(oversizedText.IsError);
            Assert.DoesNotContain(
                oversizedText.Content,
                block => block is ImageContentBlock);
            var oversizedError = GetRequiredProperty(
                GetStructured(oversizedText.StructuredContent),
                "error");
            var oversizedDetails = GetRequiredProperty(
                oversizedError,
                "details");
            Assert.Equal(
                "python_result_too_large",
                GetRequiredProperty(oversizedDetails, "reason").GetString());
            Assert.True(
                GetRequiredProperty(
                    oversizedDetails,
                    "executionCompleted").GetBoolean());
            Assert.Equal(
                0,
                GetRequiredProperty(oversizedDetails, "imageCount").GetInt32());
            Assert.True(
                GetRequiredProperty(
                    oversizedDetails,
                    "serializedCallToolResultBytes").GetInt64() >
                GetRequiredProperty(
                    oversizedDetails,
                    "maxCallToolResultBytes").GetInt64());

            var afterOversized = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = "print(payload_marker)"
                });
            Assert.Equal(
                "888\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(afterOversized.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var close = await client.CallToolAsync(
                "work_close",
                new Dictionary<string, object?> { ["workId"] = workId });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(close.StructuredContent),
                    "ok").GetBoolean());
        }
        finally
        {
            try
            {
                Directory.Delete(hostWorkingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task StdioAdapterCanUseFilesystemBridgeInsidePython()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-fs-bridge-{Guid.NewGuid():N}");
        var hostWorkingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-fs-host-{Guid.NewGuid():N}");

        Directory.CreateDirectory(
            Path.Combine(scratch, "src"));
        Directory.CreateDirectory(hostWorkingDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(scratch, "src", "a.txt"),
            "first line\nneedle one\nlast line");
        await File.WriteAllTextAsync(
            Path.Combine(scratch, "src", "b.txt"),
            "needle two");
        await File.WriteAllTextAsync(
            Path.Combine(scratch, "root.txt"),
            "root text");
        CreateSimplePdf(
            Path.Combine(scratch, "sample.pdf"),
            "Bridge PDF text");

        try
        {
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = "LoomLCI Python filesystem bridge integration test",
                    Command = "dotnet",
                    Arguments = [hostDll],
                    WorkingDirectory = hostWorkingDirectory,
                    ShutdownTimeout = TimeSpan.FromSeconds(5)
                });

            await using var client =
                await McpClient.CreateAsync(transport);

            var create = await client.CallToolAsync(
                "work_create",
                new Dictionary<string, object?>
                {
                    ["baseDirectory"] = scratch,
                    ["label"] = "python-filesystem-bridge-test"
                });

            var createRoot =
                GetStructured(create.StructuredContent);
            Assert.True(
                GetRequiredProperty(
                    createRoot,
                    "ok").GetBoolean());
            var workId = GetRequiredProperty(
                    GetRequiredProperty(
                        createRoot,
                        "result"),
                    "workId")
                .GetString();
            Assert.False(
                string.IsNullOrWhiteSpace(workId));

            var code = """
import loom
import loom.fs

expected = [
    "fs.apply_patch",
    "fs.find_paths",
    "fs.list_tree",
    "fs.manage_directory",
    "fs.read_files",
    "fs.read_pdf",
    "fs.search_text",
    "process.read",
    "process.release",
    "process.resize",
    "process.run",
    "process.run_many",
    "process.start",
    "process.status",
    "process.terminate",
    "process.write",
]
assert loom.capabilities() == expected

tree1 = loom.fs.list_tree(".", max_depth=2, max_entries=2)
assert tree1["truncated"] is True
assert tree1["next_cursor"]
tree2 = loom.fs.list_tree(
    ".",
    max_depth=2,
    max_entries=100,
    cursor=tree1["next_cursor"],
)
assert not (
    {item["path"] for item in tree1["entries"]}
    & {item["path"] for item in tree2["entries"]}
)

found1 = loom.fs.find_paths(
    ".",
    [".txt"],
    match_mode="suffix",
    type="file",
    max_results=1,
)
assert found1["truncated"] is True
found2 = loom.fs.find_paths(
    ".",
    [".txt"],
    match_mode="suffix",
    type="file",
    max_results=10,
    cursor=found1["next_cursor"],
)
assert found2["matches"]

search1 = loom.fs.search_text(
    ".",
    ["needle"],
    max_results=1,
    context_lines=1,
)
assert search1["result_limit_reached"] is True
assert search1["next_cursor"]
search2 = loom.fs.search_text(
    ".",
    ["needle"],
    max_results=10,
    context_lines=1,
    cursor=search1["next_cursor"],
)
assert search2["matches"]

read = loom.fs.read_files([
    {"path": "src/a.txt", "offset": 2, "limit": 1}
])
file = read["files"][0]
assert file["text"] == "needle one"
assert file["start_line"] == 2
assert file["has_more_before"] is True
assert file["has_more_after"] is True

patched = loom.fs.apply_patch([
    {
        "op": "replace",
        "path": "src/a.txt",
        "old_text": "needle one",
        "new_text": "patched one",
        "expected_occurrences": 1,
    }
])
assert patched["applied_changes"] == 1

created = loom.fs.manage_directory("create", "temp-dir")
assert created["exists"] is True
deleted = loom.fs.manage_directory("delete", "temp-dir")
assert deleted["exists"] is False

pdf = loom.fs.read_pdf(
    "sample.pdf",
    start_page=1,
    max_pages=1,
)
assert pdf["page_count"] == 1
assert "Bridge PDF text" in pdf["pages"][0]["text"]

try:
    loom.fs.read_files([{"path": "missing.txt"}])
    raise AssertionError("missing file unexpectedly succeeded")
except loom.LoomError as exc:
    assert exc.code == "not_found"

state_marker = 73
print("P21_FS_OK")
""";

            var execute = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] = code,
                    ["timeoutSeconds"] = 120
                });

            var executeRoot =
                GetStructured(execute.StructuredContent);
            Assert.True(
                GetRequiredProperty(
                    executeRoot,
                    "ok").GetBoolean());
            var executeResult = GetRequiredProperty(
                executeRoot,
                "result");
            Assert.Equal(
                "completed",
                GetRequiredProperty(
                    executeResult,
                    "status").GetString());
            Assert.Equal(
                "P21_FS_OK\n",
                GetRequiredProperty(
                    executeResult,
                    "stdout").GetString());

            Assert.Contains(
                "patched one",
                await File.ReadAllTextAsync(
                    Path.Combine(
                        scratch,
                        "src",
                        "a.txt")),
                StringComparison.Ordinal);

            var second = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId,
                    ["code"] =
                        "print(state_marker)\n" +
                        "print(loom.fs.read_files([" +
                        "{'path':'src/a.txt','offset':2,'limit':1}" +
                        "])['files'][0]['text'])"
                });

            var secondRoot =
                GetStructured(second.StructuredContent);
            Assert.True(
                GetRequiredProperty(
                    secondRoot,
                    "ok").GetBoolean());
            Assert.Equal(
                "73\npatched one\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        secondRoot,
                        "result"),
                    "stdout").GetString());

            var close = await client.CallToolAsync(
                "work_close",
                new Dictionary<string, object?>
                {
                    ["workId"] = workId
                });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(
                        close.StructuredContent),
                    "ok").GetBoolean());
        }
        finally
        {
            foreach (var path in new[]
                     {
                         scratch,
                         hostWorkingDirectory
                     })
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(
                            path,
                            recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task StdioAdapterCanUseProcessBridgeInsidePython()
    {
        var repoRoot = FindRepoRoot();
        var hostDll = GetHostDll(repoRoot);
        Assert.True(File.Exists(hostDll), $"Host was not built: {hostDll}");

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-process-bridge-{Guid.NewGuid():N}");
        var hostWorkingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-process-host-{Guid.NewGuid():N}");

        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(hostWorkingDirectory);

        try
        {
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = "LoomLCI Python process bridge integration test",
                    Command = "dotnet",
                    Arguments = [hostDll],
                    WorkingDirectory = hostWorkingDirectory,
                    ShutdownTimeout = TimeSpan.FromSeconds(5)
                });

            await using var client =
                await McpClient.CreateAsync(transport);

            async Task<string> CreateWorkAsync(string label)
            {
                var created = await client.CallToolAsync(
                    "work_create",
                    new Dictionary<string, object?>
                    {
                        ["baseDirectory"] = scratch,
                        ["label"] = label
                    });

                var root = GetStructured(
                    created.StructuredContent);
                Assert.True(
                    GetRequiredProperty(
                        root,
                        "ok").GetBoolean());

                return GetRequiredProperty(
                        GetRequiredProperty(
                            root,
                            "result"),
                        "workId")
                    .GetString()!;
            }

            async Task<int> ReadPidFileAsync(string path)
            {
                for (var i = 0; i < 200; i++)
                {
                    if (File.Exists(path))
                    {
                        var text = (await File.ReadAllTextAsync(path)).Trim();
                        if (int.TryParse(text, out var pid))
                        {
                            return pid;
                        }
                    }

                    await Task.Delay(25);
                }

                throw new TimeoutException(
                    $"PID file was not written: {path}");
            }

            var workA = await CreateWorkAsync(
                "python-process-a");
            var workB = await CreateWorkAsync(
                "python-process-b");

            var firstCode = """
import time
import loom
import loom.process

assert len(loom.capabilities()) == 16

quick = loom.process.run(
    "cmd.exe",
    ["/d", "/s", "/c", "echo quick-out & echo quick-err 1>&2 & exit /b 7"],
    timeout_seconds=5,
    max_output_chars=4096,
)
assert quick["exit_code"] == 7
assert "quick-out" in quick["stdout"]
assert "quick-err" in quick["stderr"]

batch = loom.process.run_many(
    [
        {"id":"first","executable":"cmd.exe",
         "arguments":["/d","/s","/c","echo ONE"]},
        {"id":"second","executable":"cmd.exe",
         "arguments":["/d","/s","/c","echo TWO & exit /b 9"]},
        {"id":"third","executable":"cmd.exe",
         "arguments":["/d","/s","/c","echo THREE"]},
    ],
    max_concurrent=2,
    job_timeout_seconds=5,
    batch_timeout_seconds=12,
    max_output_chars=500,
)
assert not batch["timed_out"]
assert [job["id"] for job in batch["jobs"]] == ["first","second","third"]
assert [job["outcome"] for job in batch["jobs"]] == [
    "success","nonzero_exit","success"]
assert [job["exit_code"] for job in batch["jobs"]] == [0,9,0]
assert "ONE" in batch["jobs"][0]["stdout"]
assert "TWO" in batch["jobs"][1]["stdout"]
assert "THREE" in batch["jobs"][2]["stdout"]

proc = loom.process.start(
    "powershell.exe",
    ["-NoProfile", "-Command", "-"],
    io_mode="pipes",
)
handle = proc["process_handle"]
loom.process.write(handle, "Write-Output PIPE_READY\n")

stdout_cursor = 0
stderr_cursor = 0
captured = ""
for _ in range(100):
    read = loom.process.read(
        handle,
        stdout_cursor=stdout_cursor,
        stderr_cursor=stderr_cursor,
        max_chars=4096,
    )
    captured += "".join(
        chunk["text"] for chunk in read["stdout"]["chunks"]
    )
    stdout_cursor = read["stdout"]["next_cursor"]
    stderr_cursor = read["stderr"]["next_cursor"]
    if "PIPE_READY" in captured:
        break
    time.sleep(0.02)

assert "PIPE_READY" in captured
print(handle)
print(proc["process_id"])
print("P22_PIPE_OK")
""";

            var first = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA,
                    ["code"] = firstCode,
                    ["timeoutSeconds"] = 120
                });

            var firstRoot = GetStructured(
                first.StructuredContent);
            Assert.True(
                GetRequiredProperty(
                    firstRoot,
                    "ok").GetBoolean());

            var firstResult = GetRequiredProperty(
                firstRoot,
                "result");
            Assert.Equal(
                "completed",
                GetRequiredProperty(
                    firstResult,
                    "status").GetString());

            var firstLines = GetRequiredProperty(
                    firstResult,
                    "stdout")
                .GetString()!
                .Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(3, firstLines.Length);
            var pipeHandle = firstLines[0];
            Assert.True(
                int.TryParse(
                    firstLines[1],
                    out var pipePid));
            Assert.Equal(
                "P22_PIPE_OK",
                firstLines[2]);

            var reset = await client.CallToolAsync(
                "python_reset",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA
                });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(
                        reset.StructuredContent),
                    "ok").GetBoolean());

            var pipeHandleLiteral =
                JsonSerializer.Serialize(pipeHandle);
            var afterResetCode =
                "import time, loom.process\n" +
                $"h = {pipeHandleLiteral}\n" +
                "status = loom.process.status(h)\n" +
                "assert status['state'] == 'running'\n" +
                "loom.process.write(h, 'Write-Output AFTER_RESET\\n')\n" +
                "cursor = 0\n" +
                "captured = ''\n" +
                "for _ in range(100):\n" +
                "    read = loom.process.read(h, stdout_cursor=cursor, max_chars=4096)\n" +
                "    captured += ''.join(chunk['text'] for chunk in read['stdout']['chunks'])\n" +
                "    cursor = read['stdout']['next_cursor']\n" +
                "    if 'AFTER_RESET' in captured:\n" +
                "        break\n" +
                "    time.sleep(0.02)\n" +
                "assert 'AFTER_RESET' in captured\n" +
                "print('P22_RESET_OK')";

            var afterReset = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA,
                    ["code"] = afterResetCode,
                    ["timeoutSeconds"] = 120
                });

            var afterResetRoot = GetStructured(
                afterReset.StructuredContent);
            Assert.True(
                GetRequiredProperty(
                    afterResetRoot,
                    "ok").GetBoolean());
            Assert.Equal(
                "P22_RESET_OK\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        afterResetRoot,
                        "result"),
                    "stdout").GetString());

            var sameSession = await client.CallToolAsync(
                "process_start",
                new Dictionary<string, object?>
                {
                    ["executable"] = "powershell.exe",
                    ["arguments"] = new[]
                    {
                        "-NoProfile",
                        "-Command",
                        "Start-Sleep -Seconds 30"
                    },
                    ["workId"] = workA
                });

            var sameSessionRoot = GetStructured(
                sameSession.StructuredContent);
            Assert.True(
                GetRequiredProperty(
                    sameSessionRoot,
                    "ok").GetBoolean());
            var sameSessionHandle = GetRequiredProperty(
                    GetRequiredProperty(
                        sameSessionRoot,
                        "result"),
                    "processHandle")
                .GetString()!;

            var sameHandleLiteral =
                JsonSerializer.Serialize(
                    sameSessionHandle);
            var sameSessionCheck =
                await client.CallToolAsync(
                    "python_execute",
                    new Dictionary<string, object?>
                    {
                        ["workId"] = workA,
                        ["code"] =
                            "import loom.process\n" +
                            $"h = {sameHandleLiteral}\n" +
                            "assert loom.process.status(h)['state'] == 'running'\n" +
                            "print('SAME_SESSION_OK')"
                    });

            Assert.Equal(
                "SAME_SESSION_OK\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(
                            sameSessionCheck.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var deniedOther =
                await client.CallToolAsync(
                    "python_execute",
                    new Dictionary<string, object?>
                    {
                        ["workId"] = workB,
                        ["code"] =
                            "import loom\n" +
                            "import loom.process\n" +
                            $"h = {sameHandleLiteral}\n" +
                            "try:\n" +
                            "    loom.process.status(h)\n" +
                            "    raise AssertionError('unexpected success')\n" +
                            "except loom.LoomError as exc:\n" +
                            "    print(exc.code)"
                    });

            Assert.Equal(
                "access_denied\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(
                            deniedOther.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var independent = await client.CallToolAsync(
                "process_start",
                new Dictionary<string, object?>
                {
                    ["executable"] = "powershell.exe",
                    ["arguments"] = new[]
                    {
                        "-NoProfile",
                        "-Command",
                        "Start-Sleep -Seconds 30"
                    },
                    ["independent"] = true
                });

            var independentRoot = GetStructured(
                independent.StructuredContent);
            Assert.True(
                GetRequiredProperty(
                    independentRoot,
                    "ok").GetBoolean());
            var independentHandle =
                GetRequiredProperty(
                        GetRequiredProperty(
                            independentRoot,
                            "result"),
                        "processHandle")
                    .GetString()!;
            var independentLiteral =
                JsonSerializer.Serialize(
                    independentHandle);

            var deniedIndependent =
                await client.CallToolAsync(
                    "python_execute",
                    new Dictionary<string, object?>
                    {
                        ["workId"] = workA,
                        ["code"] =
                            "import loom\n" +
                            "import loom.process\n" +
                            $"h = {independentLiteral}\n" +
                            "try:\n" +
                            "    loom.process.status(h)\n" +
                            "    raise AssertionError('unexpected success')\n" +
                            "except loom.LoomError as exc:\n" +
                            "    print(exc.code)"
                    });

            Assert.Equal(
                "access_denied\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(
                            deniedIndependent.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var terminateIndependent =
                await client.CallToolAsync(
                    "process_terminate",
                    new Dictionary<string, object?>
                    {
                        ["processHandle"] =
                            independentHandle
                    });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(
                        terminateIndependent.StructuredContent),
                    "ok").GetBoolean());

            var releaseIndependent =
                await client.CallToolAsync(
                    "process_release",
                    new Dictionary<string, object?>
                    {
                        ["processHandle"] =
                            independentHandle
                    });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(
                        releaseIndependent.StructuredContent),
                    "ok").GetBoolean());

            var terminalCode = """
import time
import loom.process

term = loom.process.start(
    "cmd.exe",
    ["/q"],
    io_mode="terminal",
    terminal_columns=80,
    terminal_rows=24,
)
h = term["process_handle"]
assert loom.process.resize(h, 100, 30) is True
assert loom.process.write(h, "echo TERM_OK\r\n") is True

cursor = 0
captured = ""
for _ in range(100):
    read = loom.process.read(
        h,
        terminal_cursor=cursor,
        max_chars=4096,
    )
    captured += "".join(
        chunk["text"] for chunk in read["terminal"]["chunks"]
    )
    cursor = read["terminal"]["next_cursor"]
    if "TERM_OK" in captured:
        break
    time.sleep(0.02)

assert "TERM_OK" in captured
assert loom.process.terminate(h) is True
assert loom.process.release(h) is True
print("P22_TERM_OK")
""";

            var terminal = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA,
                    ["code"] = terminalCode,
                    ["timeoutSeconds"] = 120
                });

            Assert.Equal(
                "P22_TERM_OK\n",
                GetRequiredProperty(
                    GetRequiredProperty(
                        GetStructured(
                            terminal.StructuredContent),
                        "result"),
                    "stdout").GetString());

            var runTimeoutPidPath = Path.Combine(
                scratch,
                "run-timeout.pid");
            var runTimeoutPidLiteral =
                JsonSerializer.Serialize(runTimeoutPidPath);
            var runTimeoutCode =
                "import loom.process\n" +
                $"path = {runTimeoutPidLiteral}\n" +
                "cmd = \"Set-Content -LiteralPath '\" + path.replace(\"'\", \"''\") + \"' -Value $PID; Start-Sleep -Seconds 30\"\n" +
                "loom.process.run(" +
                "'powershell.exe'," +
                " ['-NoProfile','-Command',cmd]," +
                " timeout_seconds=30)";

            var runTimeout = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA,
                    ["code"] = runTimeoutCode,
                    ["timeoutSeconds"] = 1
                });
            var runTimeoutRoot = GetStructured(
                runTimeout.StructuredContent);
            Assert.False(
                GetRequiredProperty(
                    runTimeoutRoot,
                    "ok").GetBoolean());
            Assert.Equal(
                "deadline_exceeded",
                GetRequiredProperty(
                        GetRequiredProperty(
                            runTimeoutRoot,
                            "error"),
                        "code")
                    .GetString());

            var runTimeoutPid =
                await ReadPidFileAsync(runTimeoutPidPath);
            await WaitForProcessGoneAsync(runTimeoutPid);

            var durableTimeoutPidPath = Path.Combine(
                scratch,
                "durable-timeout.pid");
            var durableTimeoutPidLiteral =
                JsonSerializer.Serialize(
                    durableTimeoutPidPath);
            var durableTimeoutCode =
                "import os, time, loom.process\n" +
                $"path = {durableTimeoutPidLiteral}\n" +
                "cmd = \"Set-Content -LiteralPath '\" + path.replace(\"'\", \"''\") + \"' -Value $PID; Start-Sleep -Seconds 30\"\n" +
                "loom.process.start(" +
                "'powershell.exe'," +
                " ['-NoProfile','-Command',cmd])\n" +
                "deadline = time.time() + 5\n" +
                "while not os.path.exists(path) and time.time() < deadline:\n" +
                "    time.sleep(0.02)\n" +
                "time.sleep(30)";

            var durableTimeout = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA,
                    ["code"] = durableTimeoutCode,
                    ["timeoutSeconds"] = 1
                });
            var durableTimeoutRoot = GetStructured(
                durableTimeout.StructuredContent);
            Assert.False(
                GetRequiredProperty(
                    durableTimeoutRoot,
                    "ok").GetBoolean());
            Assert.Equal(
                "deadline_exceeded",
                GetRequiredProperty(
                        GetRequiredProperty(
                            durableTimeoutRoot,
                            "error"),
                        "code")
                    .GetString());

            var durableTimeoutPid =
                await ReadPidFileAsync(durableTimeoutPidPath);
            Assert.True(IsProcessAlive(durableTimeoutPid));

            var cleanup = await client.CallToolAsync(
                "python_execute",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA,
                    ["code"] =
                        "import loom.process\n" +
                        "p = loom.process.start(" +
                        "'powershell.exe'," +
                        " ['-NoProfile','-Command','Start-Sleep -Seconds 30'])\n" +
                        "print(p['process_handle'])\n" +
                        "print(p['process_id'])"
                });

            var cleanupLines =
                GetRequiredProperty(
                        GetRequiredProperty(
                            GetStructured(
                                cleanup.StructuredContent),
                            "result"),
                        "stdout")
                    .GetString()!
                    .Split(
                        ['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, cleanupLines.Length);
            var cleanupHandle = cleanupLines[0];
            Assert.True(
                int.TryParse(
                    cleanupLines[1],
                    out var cleanupPid));

            var closeA = await client.CallToolAsync(
                "work_close",
                new Dictionary<string, object?>
                {
                    ["workId"] = workA
                });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(
                        closeA.StructuredContent),
                    "ok").GetBoolean());

            var afterClose = await client.CallToolAsync(
                "process_status",
                new Dictionary<string, object?>
                {
                    ["processHandle"] =
                        cleanupHandle
                });
            var afterCloseRoot = GetStructured(
                afterClose.StructuredContent);
            Assert.False(
                GetRequiredProperty(
                    afterCloseRoot,
                    "ok").GetBoolean());
            Assert.Equal(
                "resource_closed",
                GetRequiredProperty(
                        GetRequiredProperty(
                            afterCloseRoot,
                            "error"),
                        "code")
                    .GetString());

            await WaitForProcessGoneAsync(
                cleanupPid);
            await WaitForProcessGoneAsync(
                pipePid);
            await WaitForProcessGoneAsync(
                durableTimeoutPid);

            var closeB = await client.CallToolAsync(
                "work_close",
                new Dictionary<string, object?>
                {
                    ["workId"] = workB
                });
            Assert.True(
                GetRequiredProperty(
                    GetStructured(
                        closeB.StructuredContent),
                    "ok").GetBoolean());
        }
        finally
        {
            foreach (var path in new[]
                     {
                         scratch,
                         hostWorkingDirectory
                     })
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(
                            path,
                            recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process =
                global::System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitForProcessGoneAsync(int pid)
    {
        for (var i = 0; i < 200; i++)
        {
            if (!IsProcessAlive(pid))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Process {pid} remained alive.");
    }

    private static void CreateSimplePdf(string path, params string[] pageTexts)
    {
        if (pageTexts.Length == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pageTexts));
        }

        var objects = new List<(int Number, string Body)>();
        objects.Add((1, "<< /Type /Catalog /Pages 2 0 R >>"));

        var pageNumbers = Enumerable.Range(0, pageTexts.Length)
            .Select(index => 4 + (index * 2))
            .ToArray();
        objects.Add((2,
            $"<< /Type /Pages /Kids [{string.Join(" ", pageNumbers.Select(number => $"{number} 0 R"))}] /Count {pageTexts.Length} >>"));
        objects.Add((3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        for (var index = 0; index < pageTexts.Length; index++)
        {
            var pageNumber = pageNumbers[index];
            var contentNumber = pageNumber + 1;
            var escaped = pageTexts[index]
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("(", "\\(", StringComparison.Ordinal)
                .Replace(")", "\\)", StringComparison.Ordinal);
            var stream = $"BT\n/F1 12 Tf\n72 720 Td\n({escaped}) Tj\nET\n";

            objects.Add((pageNumber,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentNumber} 0 R >>"));
            objects.Add((contentNumber,
                $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream"));
        }

        objects.Sort((left, right) => left.Number.CompareTo(right.Number));
        using var memory = new MemoryStream();
        using var writer = new StreamWriter(memory, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\n"
        };

        writer.Write("%PDF-1.4\n");
        writer.Flush();
        var offsets = new Dictionary<int, long>();
        foreach (var (number, body) in objects)
        {
            offsets[number] = memory.Position;
            writer.Write($"{number} 0 obj\n{body}\nendobj\n");
            writer.Flush();
        }

        var xref = memory.Position;
        var maxObject = objects.Max(item => item.Number);
        writer.Write($"xref\n0 {maxObject + 1}\n");
        writer.Write("0000000000 65535 f \n");
        for (var number = 1; number <= maxObject; number++)
        {
            writer.Write(offsets.TryGetValue(number, out var offset)
                ? $"{offset:0000000000} 00000 n \n"
                : "0000000000 00000 f \n");
        }
        writer.Write($"trailer\n<< /Size {maxObject + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();

        File.WriteAllBytes(path, memory.ToArray());
    }

    private static string GetHostDll(string repoRoot)
    {
        var hostDll = Environment.GetEnvironmentVariable("LOOMLCI_TEST_HOST_DLL");
        if (!string.IsNullOrWhiteSpace(hostDll))
        {
            return hostDll;
        }

        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configurationDirectory = outputDirectory;
        while (configurationDirectory.Parent is not null &&
               !string.Equals(
                   configurationDirectory.Parent.Name,
                   "bin",
                   StringComparison.OrdinalIgnoreCase))
        {
            configurationDirectory = configurationDirectory.Parent;
        }

        if (configurationDirectory.Parent is null)
        {
            throw new DirectoryNotFoundException(
                "Could not infer test build configuration from AppContext.BaseDirectory.");
        }

        var relativeOutput = Path.GetRelativePath(
            configurationDirectory.FullName,
            outputDirectory.FullName);
        var targetFramework = relativeOutput.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)[0];

        return Path.Combine(
            repoRoot,
            "src",
            "LoomLCI.Host",
            "bin",
            configurationDirectory.Name,
            targetFramework,
            "LoomLCI.Host.dll");
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
