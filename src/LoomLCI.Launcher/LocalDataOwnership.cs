using System.Text.Json;

namespace LoomLCI.Launcher;

internal sealed record DataOwnership(int SchemaVersion, string InstallRoot, string DataRoot);

/// <summary>Explicit purge limited to a marked deployment. Shared caches and runtimes are out of scope.</summary>
public static class LocalDataOwnership
{
    private const string MarkerName = ".loomlci-data-owner.json";

    public static void EnsureAvailable(AppPaths paths)
    {
        var marker = Marker(paths);
        if (File.Exists(marker) && !Matches(ReadMarker(marker), paths))
            throw new InvalidOperationException("Data root identificado con otra instalación.");
    }

    public static void Record(AppPaths paths)
    {
        EnsureAvailable(paths);
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(Marker(paths), JsonSerializer.Serialize(
            new DataOwnership(1, Path.GetFullPath(paths.InstallRoot), Path.GetFullPath(paths.DataRoot))));
    }

    public static void Purge(AppPaths paths)
    {
        ValidatePurgePath(paths);
        var root = Path.GetFullPath(paths.DataRoot);
        if (!Directory.Exists(root) || !Matches(ReadMarker(Marker(paths)), paths))
            throw new InvalidOperationException("Purga denegada: falta prueba de propiedad de deployment.");

        ValidateNoReparseAncestors(root);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Purga denegada: symlink o junction presente.");
                if (Directory.Exists(entry))
                    directories.Push(entry);
            }
        }
        Directory.Delete(root, recursive: true);
    }

    private static string Marker(AppPaths paths) => Path.Combine(paths.DataRoot, MarkerName);

    private static DataOwnership? ReadMarker(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DataOwnership>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    private static bool Matches(DataOwnership? owner, AppPaths paths) =>
        owner is { SchemaVersion: 1, InstallRoot: { Length: > 0 }, DataRoot: { Length: > 0 } } &&
        SamePath(owner.InstallRoot, paths.InstallRoot) &&
        SamePath(owner.DataRoot, paths.DataRoot);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);

    private static void ValidatePurgePath(AppPaths paths)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.DataRoot));
        var local = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.InstallRoot));
        if (!Path.GetFileName(root).Equals("deployment", StringComparison.OrdinalIgnoreCase) ||
            !root.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            SamePath(root, install) ||
            root.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            install.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Purga denegada: ruta fuera del deployment dedicado.");
    }

    private static void ValidateNoReparseAncestors(string path)
    {
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) &&
                (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Purga denegada: ruta con junction o symlink.");
    }
}
