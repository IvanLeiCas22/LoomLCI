using System.Text;
using LoomLCI.Windows.Processes;

namespace LoomLCI.Windows.Tests;

public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a b\\", "\"a b\\\\\"")]
    public void AppendArgumentUsesWindowsCommandLineEscaping(
        string argument,
        string expected)
    {
        var builder = new StringBuilder();

        WindowsCommandLine.AppendArgument(builder, argument);

        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void BuildQuotesExecutableAndKeepsArgumentBoundaries()
    {
        var commandLine = WindowsCommandLine.Build(
            @"C:\Program Files\tool.exe",
            ["", "plain", "two words"]);

        Assert.Equal(
            "\"C:\\Program Files\\tool.exe\" \"\" plain \"two words\"",
            commandLine);
    }
}
