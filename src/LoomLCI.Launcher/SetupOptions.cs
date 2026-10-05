namespace LoomLCI.Launcher;

public sealed record SetupOptions(
    string? TunnelId,
    string? RuntimeKeyFile,
    string? Alias,
    string? PackageRoot,
    string? InstallRoot,
    string? DataRoot,
    bool NoShortcut)
{
    public static SetupOptions Parse(string[] args)
    {
        string? tunnelId = null;
        string? runtimeKeyFile = null;
        string? alias = null;
        string? packageRoot = null;
        string? installRoot = null;
        string? dataRoot = null;
        var noShortcut = false;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            switch (current)
            {
                case "--tunnel-id":
                    tunnelId = Next(args, ref i, current);
                    break;
                case "--runtime-key-file":
                    runtimeKeyFile = Next(args, ref i, current);
                    break;
                case "--alias":
                    alias = Next(args, ref i, current);
                    break;
                case "--package-root":
                    packageRoot = Next(args, ref i, current);
                    break;
                case "--install-root":
                    installRoot = Next(args, ref i, current);
                    break;
                case "--data-root":
                    dataRoot = Next(args, ref i, current);
                    break;
                case "--no-shortcut":
                    noShortcut = true;
                    break;
                default:
                    throw new ArgumentException($"Opción de setup desconocida: {current}");
            }
        }

        return new SetupOptions(
            tunnelId,
            runtimeKeyFile,
            alias,
            packageRoot,
            installRoot,
            dataRoot,
            noShortcut);
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Falta valor para {option}.");
        }

        index++;
        return args[index];
    }
}
