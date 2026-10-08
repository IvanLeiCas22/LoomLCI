// A3.1: isolated Streamable HTTP experiment. Not registered with the production Launcher.
// No runtime key from Secure MCP Tunnel is used here; this process requires its own secret.
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
using LoomLCI.Mcp;
using LoomLCI.PdfWorker;
using LoomLCI.Windows.Filesystem;
using LoomLCI.Windows.Processes;
using LoomLCI.Windows.Python;
using LoomLCI.Windows.VisualFiles;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

if (args.Length == 1 &&
    string.Equals(args[0], "--internal-pdf-worker-v1", StringComparison.Ordinal))
{
    Environment.ExitCode = await PdfWorkerProgram.RunAsync(
        Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError());
    return;
}

if (args.Length == 1 &&
    string.Equals(args[0], "--internal-pdf-render-worker-v1", StringComparison.Ordinal))
{
    Environment.ExitCode = await PdfRenderWorkerProgram.RunAsync(
        Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError());
    return;
}

var rawPort = Environment.GetEnvironmentVariable("LOOMLCI_HTTP_POC_PORT");
if (!int.TryParse(rawPort, out var port) || port is < 1024 or > 65535)
{
    throw new InvalidOperationException("Set LOOMLCI_HTTP_POC_PORT (1024-65535) before starting the HTTP PoC.");
}

var secret = Environment.GetEnvironmentVariable("LOOMLCI_HTTP_POC_TOKEN");
if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32 ||
    !string.Equals(secret, secret.Trim(), StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Set LOOMLCI_HTTP_POC_TOKEN to a fresh random secret with at least 32 UTF-8 bytes.");
}
var secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));

var processPath = Environment.ProcessPath
    ?? throw new InvalidOperationException("Unable to locate the host process.");
var assemblyPath = Assembly.GetEntryAssembly()?.Location
    ?? throw new InvalidOperationException("Unable to locate the host assembly.");
var pdfWorkerLaunch = new PdfWorkerLaunchDescriptor(
    processPath,
    string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
        ? [assemblyPath] : [],
    AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

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
builder.Services.AddSingleton<IVisualFilesProvider>(_ => new WindowsVisualFilesProvider(pdfWorkerLaunch));
builder.Services.AddSingleton<VisualFilesCapability>();
builder.Services.AddSingleton<IPythonBridgeModule, PythonFilesystemBridgeModule>();
builder.Services.AddSingleton<IPythonBridgeModule, PythonProcessBridgeModule>();
builder.Services.AddSingleton<IPythonBridgeDispatcher, PythonBridgeDispatcher>();
builder.Services.AddSingleton<PythonCapability>();
builder.Services.AddHostedService<PocLifetimeSweeper>();

// Register the identical tool classes as the production STDIO Host, but a different transport.
builder.Services.AddMcpServer(options =>
    {
        options.ServerInstructions =
            "LoomLCI HTTP A3.1 isolated test. Full Trust Windows tools. " +
            "Use work_create to own stateful resources, and work_close to release them.";
    })
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<WorkTools>()
    .WithTools<ProcessTools>()
    .WithTools<FilesystemTools>()
    .WithTools<VisualFilesTools>()
    .WithTools<PythonTools>()
    .WithTools<WorkPlanTools>();

var app = builder.Build();

// Applies to ALL routes. The PoC refuses every non-loopback Host or browser Origin
// (rather than enabling CORS). The loopback binding alone is not authentication.
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (!string.Equals(request.Host.Host, "127.0.0.1", StringComparison.Ordinal) ||
        request.Host.Port != port ||
        request.Headers.ContainsKey("Origin"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // OAuth Protected Resource Metadata discovery is public by specification.
    // This PoC does not offer OAuth. Reply with an empty 404 on only the two
    // well-known discovery GET paths; every operational endpoint still requires
    // its independent, unguessable local Bearer token.
    if (HttpMethods.IsGet(request.Method) &&
        (string.Equals(request.Path.Value, "/.well-known/oauth-protected-resource", StringComparison.Ordinal) ||
         string.Equals(request.Path.Value, "/.well-known/oauth-protected-resource/mcp", StringComparison.Ordinal)))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }

    var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]));
    if (!CryptographicOperations.FixedTimeEquals(secretHash, presentedHash))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ready", mode = "a3.1-http-poc" }));
app.MapMcp("/mcp");
await app.RunAsync();

// Same expiration mechanics as the production Host, private to the experimental process.
sealed class PocLifetimeSweeper(
    WorkSessionManager sessions,
    ProcessCapability processes,
    LifetimeOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await processes.SweepExpiredAsync();
                await sessions.SweepExpiredAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
