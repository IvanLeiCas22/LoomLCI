#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsEnvironmentBlock
{
    private readonly char[]? _characters;

    private WindowsEnvironmentBlock(char[]? characters)
    {
        _characters = characters;
    }

    public bool IsInherited => _characters is null;

    public static WindowsEnvironmentBlock Create(
        IReadOnlyDictionary<string, string?> overrides)
    {
        if (overrides.Count == 0)
        {
            return new WindowsEnvironmentBlock(null);
        }

        var values = ReadCurrentEnvironment();

        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrEmpty(key) || key.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Environment variable names must be non-empty and cannot contain NUL.", nameof(overrides));
            }

            if (key[0] != '=' && key.Contains('='))
            {
                throw new ArgumentException($"Environment variable name '{key}' cannot contain '='.", nameof(overrides));
            }

            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                if (value.IndexOf('\0') >= 0)
                {
                    throw new ArgumentException($"Environment variable '{key}' contains NUL.", nameof(overrides));
                }

                values[key] = value;
            }
        }

        var builder = new StringBuilder();
        foreach (var (key, value) in values)
        {
            builder.Append(key);
            builder.Append('=');
            builder.Append(value);
            builder.Append('\0');
        }

        builder.Append('\0');
        return new WindowsEnvironmentBlock(builder.ToString().ToCharArray());
    }

    internal char[]? Characters => _characters;

    private static unsafe SortedDictionary<string, string> ReadCurrentEnvironment()
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = PInvoke.GetEnvironmentStringsW();
        if (block.Value is null)
        {
            throw new InvalidOperationException("Windows did not provide an environment block.");
        }

        try
        {
            var current = block.Value;
            while (*current != '\0')
            {
                var entry = new string(current);
                var separator = entry[0] == '='
                    ? entry.IndexOf('=', 1)
                    : entry.IndexOf('=');

                if (separator > 0)
                {
                    result[entry[..separator]] = entry[(separator + 1)..];
                }

                current += entry.Length + 1;
            }
        }
        finally
        {
            if (!PInvoke.FreeEnvironmentStrings(block.Value))
            {
                throw new InvalidOperationException("Windows could not release the environment block.");
            }
        }

        return result;
    }
}
