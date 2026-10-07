using System.Reflection;
using LoomLCI.Core.AgentSupport;
using LoomLCI.Core.Filesystem;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Core.VisualFiles;
using LoomLCI.Host;
using LoomLCI.Mcp;
using LoomLCI.PdfWorker;
using LoomLCI.Windows.Filesystem;
using LoomLCI.Windows.Processes;
using LoomLCI.Windows.Python;
using LoomLCI.Windows.VisualFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length == 1 &&
    string.Equals(args[0], "--internal-pdf-worker-v1", StringComparison.Ordinal))
{
    Environment.ExitCode = await PdfWorkerProgram.RunAsync(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        Console.OpenStandardError());
    return;
}

if (args.Length == 1 &&
    string.Equals(args[0], "--internal-pdf-render-worker-v1", StringComparison.Ordinal))
{
    Environment.ExitCode = await PdfRenderWorkerProgram.RunAsync(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        Console.OpenStandardError());
    return;
}

var processPath = Environment.ProcessPath
    ?? throw new InvalidOperationException("Could not resolve the current Host process path.");
var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location
    ?? throw new InvalidOperationException("Could not resolve the current Host entry assembly.");
var processName = Path.GetFileNameWithoutExtension(processPath);
var workerArgumentsPrefix = string.Equals(
        processName,
        "dotnet",
        StringComparison.OrdinalIgnoreCase)
    ? new[] { entryAssemblyPath }
    : Array.Empty<string>();
var pdfWorkerLaunch = new PdfWorkerLaunchDescriptor(
    processPath,
    workerArgumentsPrefix,
    AppContext.BaseDirectory);

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
builder.Services.AddSingleton<WorkPlanCapability>();
builder.Services.AddSingleton<IProcessProvider, WindowsProcessProvider>();
builder.Services.AddSingleton<ProcessCapability>();
builder.Services.AddSingleton<IPythonRuntimeProvider, WindowsPythonRuntimeProvider>();
builder.Services.AddSingleton<IPythonPackageProvider, WindowsPythonPackageProvider>();
builder.Services.AddSingleton<PythonPackageCapability>();
builder.Services.AddSingleton<IFilesystemProvider, WindowsFilesystemProvider>();
builder.Services.AddSingleton<FilesystemCapability>();
builder.Services.AddSingleton<IVisualFilesProvider>(
    _ => new WindowsVisualFilesProvider(pdfWorkerLaunch));
builder.Services.AddSingleton<VisualFilesCapability>();
builder.Services.AddSingleton<IPythonBridgeModule, PythonFilesystemBridgeModule>();
builder.Services.AddSingleton<IPythonBridgeModule, PythonProcessBridgeModule>();
builder.Services.AddSingleton<IPythonBridgeDispatcher, PythonBridgeDispatcher>();
builder.Services.AddSingleton<PythonCapability>();
builder.Services.AddHostedService<LifetimeSweeperService>();

builder.Services.AddLoomMcpStdio(enableWorkPlan: true);

await builder.Build().RunAsync();
