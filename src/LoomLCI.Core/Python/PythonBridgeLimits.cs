namespace LoomLCI.Core.Python;

public static class PythonBridgeLimits
{
    public const int MaxCallFrameBytes = 2 * 1024 * 1024;
    public const int MaxResultFrameBytes = 8 * 1024 * 1024;
    public const int MaxMethodChars = 128;
}
