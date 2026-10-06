namespace LoomLCI.Launcher;

public static class VersionName
{
    public static void Validate(string version)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            version is "." or ".." ||
            version.Length > 128 ||
            version.Any(ch =>
                !(char.IsLetterOrDigit(ch) ||
                  ch is '.' or '-' or '_')))
        {
            throw new InvalidDataException(
                $"Versión inválida: '{version}'.");
        }
    }
}
