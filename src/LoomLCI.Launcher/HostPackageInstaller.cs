namespace LoomLCI.Launcher;

public static class HostPackageInstaller
{
    public static void InstallHost(
        string packageRoot,
        PortablePackageManifest manifest,
        AppPaths paths)
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
                // Setup must never destroy a previously installed version.
                Directory.Delete(stagingVersion, recursive: true);
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


    /// <summary>Prepares and verifies a Host copy without touching installed versions.</summary>
    public static void StageHost(
        string packageRoot,
        PortablePackageManifest manifest,
        string stagingDirectory)
    {
        VersionName.Validate(manifest.Version);
        var root = Path.GetFullPath(packageRoot);
        var relative = manifest.HostRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("hostRelativePath debe ser relativo al paquete.");
        }

        var source = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root : root + Path.DirectorySeparatorChar;
        if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(source, "LoomLCI.Host.exe")))
        {
            throw new InvalidDataException("Host payload inválido o fuera del paquete.");
        }

        if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
        {
            throw new IOException("Staging de Host ya existe.");
        }

        try
        {
            CopyDirectory(source, stagingDirectory);
            VerifyCopy(source, stagingDirectory);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    private static void VerifyCopy(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var copy = Path.Combine(destination, relative);
            if (!File.Exists(copy))
            {
                throw new InvalidDataException("Falta un archivo de Host en staging.");
            }

            using var original = File.OpenRead(file);
            using var staged = File.OpenRead(copy);
            if (original.Length != staged.Length ||
                !System.Security.Cryptography.SHA256.HashData(original)
                    .AsSpan().SequenceEqual(
                        System.Security.Cryptography.SHA256.HashData(staged)))
            {
                throw new InvalidDataException("La copia de Host en staging no coincide con el paquete.");
            }
        }
    }

    internal static void CopyDirectory(
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
