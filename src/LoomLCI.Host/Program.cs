using LoomLCI.Core.Filesystem;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Host;
using LoomLCI.Mcp;
using LoomLCI.Windows.Filesystem;
using LoomLCI.Windows.Processes;
using LoomLCI.Windows.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton(new LifetimeOptions());
builder.Services.AddSingleton<LoomEventBus>();
builder.Services.AddSingleton<ResourceRegistry>();
builder.Services.AddSingleton<WorkSessionManager>();
builder.Services.AddSingleton<InvocationRunner>();
builder.Services.AddSingleton<IProcessProvider, WindowsProcessProvider>();
builder.Services.AddSingleton<ProcessCapability>();
builder.Services.AddSingleton<IPythonRuntimeProvider, WindowsPythonRuntimeProvider>();
builder.Services.AddSingleton<PythonCapability>();
builder.Services.AddSingleton<IFilesystemProvider, WindowsFilesystemProvider>();
builder.Services.AddSingleton<FilesystemCapability>();
builder.Services.AddHostedService<LifetimeSweeperService>();

builder.Services.AddLoomMcpStdio();

await builder.Build().RunAsync();
