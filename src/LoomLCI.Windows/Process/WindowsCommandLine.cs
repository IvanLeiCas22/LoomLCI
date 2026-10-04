using System.Text;

namespace LoomLCI.Windows.Processes;

internal static class WindowsCommandLine
{
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, executable);

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    internal static void AppendArgument(StringBuilder builder, string argument)
    {
        var needsQuotes = argument.Length == 0 ||
            argument.Any(character =>
                char.IsWhiteSpace(character) ||
                character == '"');

        if (!needsQuotes)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var pendingBackslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', checked(pendingBackslashes * 2 + 1));
                builder.Append('"');
                pendingBackslashes = 0;
                continue;
            }

            if (pendingBackslashes > 0)
            {
                builder.Append('\\', pendingBackslashes);
                pendingBackslashes = 0;
            }

            builder.Append(character);
        }

        if (pendingBackslashes > 0)
        {
            builder.Append('\\', checked(pendingBackslashes * 2));
        }

        builder.Append('"');
    }
}
