using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LoomLCI.Launcher;

public sealed record LauncherShortcuts(
    string? StartPath,
    string? StopPath);

internal sealed record ShortcutOwnership(
    string LauncherPath,
    string DesktopDirectory,
    string? StartSha256,
    string? StopSha256);

public static class ShortcutCreator
{
    private const string OwnershipFile = ".loomlci-shortcuts.json";
    private const string StartName = "LoomLCI.lnk";
    private const string StopName = "Detener LoomLCI.lnk";

    public static LauncherShortcuts CreateDesktopShortcuts(
        string launcherPath,
        string? desktopDirectory = null)
    {
        EnsureWindows();
        var target = Path.GetFullPath(launcherPath);
        var desktop = GetDesktop(desktopDirectory);
        Directory.CreateDirectory(desktop);

        var start = TryEnsureShortcut(
            Path.Combine(desktop, StartName), target,
            "start --pause", "Iniciar LoomLCI");
        var stop = TryEnsureShortcut(
            Path.Combine(desktop, StopName), target,
            "stop --pause", "Detener LoomLCI");

        var ownership = new ShortcutOwnership(
            target, desktop,
            start is null ? null : HashFile(start),
            stop is null ? null : HashFile(stop));
        var markerPath = Path.Combine(
            Path.GetDirectoryName(target)!, OwnershipFile);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(ownership));

        return new LauncherShortcuts(start, stop);
    }

    // Never delete a shortcut merely because its filename matches LoomLCI.
    // Only unlink a byte-for-byte owned file that still points at this Launcher.
    public static int RemoveOwnedDesktopShortcuts(
        string launcherPath,
        string? desktopDirectory = null)
    {
        EnsureWindows();
        var target = Path.GetFullPath(launcherPath);
        var markerPath = Path.Combine(
            Path.GetDirectoryName(target)!, OwnershipFile);
        if (!File.Exists(markerPath))
        {
            return 0; // Historical install without ownership evidence.
        }

        ShortcutOwnership? owner;
        try
        {
            owner = JsonSerializer.Deserialize<ShortcutOwnership>(
                File.ReadAllText(markerPath));
        }
        catch (JsonException)
        {
            return 0; // Corrupt metadata must not trigger a deletion.
        }

        var desktop = GetDesktop(desktopDirectory);
        if (owner is not { LauncherPath: { Length: > 0 }, DesktopDirectory: { Length: > 0 } } ||
            !SamePath(owner.LauncherPath, target) ||
            !SamePath(owner.DesktopDirectory, desktop))
        {
            return 0;
        }

        var deleted = 0;
        if (TryRemove(Path.Combine(desktop, StartName), target,
                "start --pause", owner.StartSha256))
        {
            deleted++;
        }
        if (TryRemove(Path.Combine(desktop, StopName), target,
                "stop --pause", owner.StopSha256))
        {
            deleted++;
        }
        return deleted;
    }

    private static string? TryEnsureShortcut(
        string path, string launcher, string arguments, string description)
    {
        if (File.Exists(path))
        {
            // Existing shortcuts from an older installation can be adopted
            // only when they already target this exact launcher/command.
            return IsExpectedShortcut(path, launcher, arguments) ? path : null;
        }

        CreateShortcut(path, launcher, arguments, description);
        return path;
    }

    private static bool TryRemove(
        string path, string launcher, string arguments, string? expectedHash)
    {
        if (string.IsNullOrEmpty(expectedHash) || !File.Exists(path) ||
            IsReparsePoint(path) ||
            !string.Equals(HashFile(path), expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !IsExpectedShortcut(path, launcher, arguments))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static bool IsExpectedShortcut(
        string path, string launcher, string arguments)
    {
        if (IsReparsePoint(path))
        {
            return false;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType)!;
                dynamic s = shell;
                shortcut = s.CreateShortcut(path);
                dynamic link = shortcut;
                return SamePath((string)link.TargetPath, launcher) &&
                    string.Equals((string)link.Arguments, arguments,
                        StringComparison.Ordinal);
            }
            finally
            {
                ReleaseCom(shortcut);
                ReleaseCom(shell);
            }
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static string GetDesktop(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : path);

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static void CreateShortcut(
        string shortcutPath, string launcherPath,
        string arguments, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host no está disponible.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("No se pudo crear WScript.Shell.");
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
            ReleaseCom(shortcut);
            ReleaseCom(shell);
        }
    }
}
