using System.Text;
using LoomLCI.Core.Observability;

namespace LoomLCI.Launcher;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false);

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("LoomLCI Launcher sólo es compatible con Windows.");
            return 2;
        }

        try
        {
            var paths = AppPaths.ForCurrentUser();
            var runner = new ProcessRunner();
            var tunnelClient = new TunnelClient(runner);
            var setup = new SetupService(tunnelClient);
            var updateRuntime = new TunnelUpdateRuntimeControl(
                tunnelClient);
            var updates = new UpdateService(
                paths,
                updateRuntime,
                feed: UpdateFeedOptions.FromEnvironmentOrDefault());
            var app = new LauncherApplication(
                paths,
                tunnelClient,
                setup,
                updates,
                Console.In,
                Console.Out,
                Console.Error);

            return await app.RunAsync(args, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Never persist exception messages or command arguments.
            DiagnosticsLog.TryAppend(AppPaths.ForCurrentUser().DataRoot,
                new DiagnosticRecord(DateTimeOffset.UtcNow, "launcher",
                    "LauncherUnhandledError", Outcome: "failure"));
            Console.Error.WriteLine($"Error inesperado: {ex.Message}");
            return 1;
        }
    }
}
