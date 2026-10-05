namespace LoomLCI.Core.Filesystem;

internal static class FilesystemPathResolver
{
    public static LoomResult<string> Resolve(
        string? requested,
        string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return LoomResult<string>.Failure(
                LoomErrors.InvalidArgument("path is required."));
        }

        try
        {
            if (Path.IsPathFullyQualified(requested))
            {
                return LoomResult<string>.Success(Path.GetFullPath(requested));
            }

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return LoomResult<string>.Failure(
                    LoomErrors.InvalidArgument(
                        "A relative path requires a work session with base_directory."));
            }

            return LoomResult<string>.Success(
                Path.GetFullPath(Path.Combine(baseDirectory, requested)));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return LoomResult<string>.Failure(
                LoomErrors.InvalidArgument($"Invalid path: {ex.Message}"));
        }
    }
}
