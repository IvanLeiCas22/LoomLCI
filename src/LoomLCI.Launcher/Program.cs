namespace LoomLCI.Launcher;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
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
            var app = new LauncherApplication(
                paths,
                tunnelClient,
                setup,
                Console.Out,
                Console.Error);

            return await app.RunAsync(args, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error inesperado: {ex.Message}");
            return 1;
        }
    }
}
