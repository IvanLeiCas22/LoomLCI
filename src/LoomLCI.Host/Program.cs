using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Mcp;
using LoomLCI.Windows.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<LoomEventBus>();
builder.Services.AddSingleton<ResourceRegistry>();
builder.Services.AddSingleton<WorkSessionManager>();
builder.Services.AddSingleton<InvocationRunner>();
builder.Services.AddSingleton<IProcessProvider, WindowsProcessProvider>();
builder.Services.AddSingleton<ProcessCapability>();

builder.Services.AddLoomMcpStdio();

await builder.Build().RunAsync();
