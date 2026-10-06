using System.Runtime.InteropServices;

namespace LoomLCI.Launcher;

public sealed record LauncherShortcuts(
    string StartPath,
    string StopPath);

public static class ShortcutCreator
{
    public static LauncherShortcuts CreateDesktopShortcuts(
        string launcherPath,
        string? desktopDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var desktop = string.IsNullOrWhiteSpace(desktopDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Path.GetFullPath(desktopDirectory);
        Directory.CreateDirectory(desktop);

        var startPath = Path.Combine(desktop, "LoomLCI.lnk");
        var stopPath = Path.Combine(desktop, "Detener LoomLCI.lnk");

        CreateShortcut(
            startPath,
            launcherPath,
            "start --pause",
            "Iniciar LoomLCI");
        CreateShortcut(
            stopPath,
            launcherPath,
            "stop --pause",
            "Detener LoomLCI");

        return new LauncherShortcuts(startPath, stopPath);
    }

    private static void CreateShortcut(
        string shortcutPath,
        string launcherPath,
        string arguments,
        string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException(
                "Windows Script Host no está disponible.");

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException(
                    "No se pudo crear WScript.Shell.");

            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);

            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = launcherPath;
            dynamicShortcut.Arguments = arguments;
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(launcherPath)!;
            dynamicShortcut.IconLocation = launcherPath + ",0";
            dynamicShortcut.Description = description;
            dynamicShortcut.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
