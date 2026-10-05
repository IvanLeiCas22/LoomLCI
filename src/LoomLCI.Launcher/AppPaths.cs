namespace LoomLCI.Launcher;

public sealed record AppPaths(string InstallRoot, string DataRoot)
{
    public string LauncherPath => Path.Combine(InstallRoot, "LoomLCI.Launcher.exe");
    public string VersionsRoot => Path.Combine(InstallRoot, "versions");
    public string ToolsRoot => Path.Combine(InstallRoot, "tools");
    public string TunnelClientPath => Path.Combine(ToolsRoot, "tunnel-client.exe");

    public string ConfigRoot => Path.Combine(DataRoot, "config");
    public string MachineConfigPath => Path.Combine(ConfigRoot, "machine.json");
    public string SecretsRoot => Path.Combine(DataRoot, "secrets");
    public string RuntimeKeyPath => Path.Combine(SecretsRoot, "runtime-api-key.txt");
    public string ProfilesRoot => Path.Combine(DataRoot, "tunnel-profiles");
    public string TunnelStateRoot => Path.Combine(DataRoot, "tunnel-state");
    public string LogsRoot => Path.Combine(DataRoot, "logs");

    public string VersionDirectory(string version) =>
        Path.Combine(VersionsRoot, version);

    public string HostPath(string version) =>
        Path.Combine(VersionDirectory(version), "LoomLCI.Host.exe");

    public static AppPaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        var installRoot = Environment.GetEnvironmentVariable("LOOMLCI_INSTALL_ROOT");
        var dataRoot = Environment.GetEnvironmentVariable("LOOMLCI_DATA_ROOT");

        return new AppPaths(
            string.IsNullOrWhiteSpace(installRoot)
                ? Path.Combine(localAppData, "Programs", "LoomLCI")
                : Path.GetFullPath(installRoot),
            string.IsNullOrWhiteSpace(dataRoot)
                ? Path.Combine(localAppData, "LoomLCI", "deployment")
                : Path.GetFullPath(dataRoot));
    }
}
