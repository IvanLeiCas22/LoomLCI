using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

        Assert.Equal(19, disabledToolRegistrations);
        Assert.Equal(21, enabledToolRegistrations);
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
            Assert.Contains("python_execute", instructions, StringComparison.Ordinal);
            Assert.Contains("Process capabilities", instructions, StringComparison.Ordinal);
            Assert.Contains("opaque values", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Work Plan", instructions, StringComparison.Ordinal);
            Assert.Contains("multiple meaningful phases", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("simple lookups", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("revision 0", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("meaningful milestones", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not after every tool call", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("on conflict", instructions, StringComparison.OrdinalIgnoreCase);
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

        var viewImage = Assert.Single(tools, tool => tool.Name == "filesystem_view_image");
        Assert.Equal("View local image", viewImage.ProtocolTool.Title);
        Assert.Contains("PNG, JPEG, or WebP", viewImage.Description, StringComparison.Ordinal);
        Assert.Contains("does not resize, convert, edit, or OCR", viewImage.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewImage.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.False(viewImage.ProtocolTool.Annotations?.DestructiveHint ?? true);
        Assert.True(viewImage.ProtocolTool.Annotations?.IdempotentHint ?? false);
        Assert.False(viewImage.ProtocolTool.Annotations?.OpenWorldHint ?? true);
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
        var readPdfProperties = GetRequiredProperty(readPdf.JsonSchema, "properties");
        GetRequiredProperty(readPdfProperties, "path");
        GetRequiredProperty(readPdfProperties, "workId");
        AssertSchemaRange(GetRequiredProperty(readPdfProperties, "startPage"), 1, int.MaxValue);
        AssertSchemaRange(GetRequiredProperty(readPdfProperties, "maxPages"), 1, 25);
        Assert.Equal(1, GetRequiredProperty(GetRequiredProperty(readPdfProperties, "startPage"), "default").GetInt32());
        Assert.Equal(10, GetRequiredProperty(GetRequiredProperty(readPdfProperties, "maxPages"), "default").GetInt32());

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
        Assert.Equal("Create or update work plan", updateWorkPlan.ProtocolTool.Title);
        Assert.Contains("multiple meaningful phases", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expectedRevision=0", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("meaningful milestones", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rather than after every tool call", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaces the complete", updateWorkPlan.Description, StringComparison.OrdinalIgnoreCase);
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
