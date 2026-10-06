namespace LoomLCI.Launcher;

public static class HostPackageInstaller
{
    public static void InstallHost(
        string packageRoot,
        PortablePackageManifest manifest,
        AppPaths paths,
        bool replaceExisting = false)
    {
        var root = Path.GetFullPath(packageRoot);
        var relative = manifest.HostRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(
                "hostRelativePath debe ser relativo al paquete.");
        }

        var sourceHost = Path.GetFullPath(
            Path.Combine(root, relative));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!sourceHost.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "hostRelativePath escapa del paquete.");
        }

        if (!Directory.Exists(sourceHost) ||
            !File.Exists(Path.Combine(sourceHost, "LoomLCI.Host.exe")))
        {
            throw new InvalidOperationException(
                $"Paquete inválido: Host payload ausente en {sourceHost}.");
        }

        VersionName.Validate(manifest.Version);

        Directory.CreateDirectory(paths.InstallRoot);
        Directory.CreateDirectory(paths.VersionsRoot);

        var finalVersion = paths.VersionDirectory(manifest.Version);
        var stagingVersion =
            finalVersion + ".staging-" + Guid.NewGuid().ToString("N");

        try
        {
            CopyDirectory(sourceHost, stagingVersion);

            if (!File.Exists(
                    Path.Combine(stagingVersion, "LoomLCI.Host.exe")))
            {
                throw new InvalidOperationException(
                    "La copia staging del Host quedó incompleta.");
            }

            if (Directory.Exists(finalVersion))
            {
                if (!replaceExisting)
                {
                    Directory.Delete(stagingVersion, recursive: true);
                }
                else
                {
                    Directory.Delete(finalVersion, recursive: true);
                    Directory.Move(stagingVersion, finalVersion);
                }
            }
            else
            {
                Directory.Move(stagingVersion, finalVersion);
            }
        }
        catch
        {
            if (Directory.Exists(stagingVersion))
            {
                try
                {
                    Directory.Delete(stagingVersion, recursive: true);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private static void CopyDirectory(
        string source,
        string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.Copy(
                file,
                Path.Combine(destination, Path.GetFileName(file)),
                overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
