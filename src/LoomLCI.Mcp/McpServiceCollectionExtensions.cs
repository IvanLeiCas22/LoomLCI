using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddLoomMcpStdio(this IServiceCollection services)
    {
        services
            .AddMcpServer(options =>
            {
                options.ServerInstructions =
                    "LoomLCI operates the user's local Windows environment with the user's normal permissions; it is not a sandbox. " +
                    "Prefer structured LoomLCI filesystem capabilities over shell commands when an equivalent operation exists. " +
                    "Use python_execute for persistent in-session calculations, parsing, and transformations; use Process capabilities for independent executables, terminal semantics, subprocess workflows, or large retained output. " +
                    "Use a work session when calls need a shared base directory or session-owned resources. " +
                    "Treat work and process handles as opaque values and pass them back unchanged. Close work sessions when their task is complete.";
            })
            .WithStdioServerTransport()
            .WithTools<WorkTools>()
            .WithTools<ProcessTools>()
            .WithTools<FilesystemTools>()
            .WithTools<PythonTools>();

        return services;
    }
}
