using System.Runtime.InteropServices;

namespace LoomLCI.Launcher;

public static class ShortcutCreator
{
    public static string CreateDesktopShortcut(string launcherPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var desktop = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "LoomLCI.lnk");

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
            dynamicShortcut.Arguments = "start";
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(launcherPath)!;
            dynamicShortcut.IconLocation = launcherPath + ",0";
            dynamicShortcut.Description = "Iniciar LoomLCI";
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

        return shortcutPath;
    }
}
