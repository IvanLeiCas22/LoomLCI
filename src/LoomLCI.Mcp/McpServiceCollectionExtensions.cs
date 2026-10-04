using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddLoomMcpStdio(
        this IServiceCollection services,
        bool enableWorkPlan = false)
    {
        var serverInstructions =
            "LoomLCI operates the user's local Windows environment with the user's normal permissions; it is not a sandbox. " +
            "Prefer structured LoomLCI filesystem capabilities over shell commands when an equivalent operation exists. " +
            "Use python_execute for persistent in-session calculations, parsing, and transformations; use Process capabilities for independent executables, terminal semantics, subprocess workflows, or large retained output. " +
            "Use a work session when calls need a shared base directory or session-owned resources. " +
            "Treat work and process handles as opaque values and pass them back unchanged. Close work sessions when their task is complete.";

        if (enableWorkPlan)
        {
            serverInstructions +=
                " Use Work Plan to organize non-trivial work with multiple meaningful phases, dependent actions, or checkpoints; skip it for simple lookups and short single-step tasks. " +
                "On a newly created WorkSession, the plan starts empty at revision 0, so you may create the initial plan directly with work_plan_update and expectedRevision=0. " +
                "Keep a concise plan with a few outcome-oriented steps and update it at meaningful milestones, not after every tool call. " +
                "Work Plan tracks logical progress only and does not execute or monitor real work. " +
                "Preserve returned Work Plan step ids and revision; on conflict, reread the plan, reconcile, and retry.";
        }

        var builder = services
            .AddMcpServer(options =>
            {
                options.ServerInstructions = serverInstructions;
            })
            .WithStdioServerTransport()
            .WithTools<WorkTools>()
            .WithTools<ProcessTools>()
            .WithTools<FilesystemTools>()
            .WithTools<PythonTools>();

        if (enableWorkPlan)
        {
            builder.WithTools<WorkPlanTools>();
        }

        return services;
    }
}
