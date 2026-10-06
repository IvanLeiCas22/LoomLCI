namespace LoomLCI.Launcher;

public static class DeploymentLock
{
    public static FileStream Acquire(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataRoot);

        try
        {
            return new FileStream(
                paths.OperationLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Otra operación de instalación/update de LoomLCI está en curso.",
                ex);
        }
    }
}
