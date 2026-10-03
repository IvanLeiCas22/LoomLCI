using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddLoomMcpStdio(this IServiceCollection services)
    {
        services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<WorkTools>()
            .WithTools<ProcessTools>()
            .WithTools<FilesystemTools>();

        return services;
    }
}
