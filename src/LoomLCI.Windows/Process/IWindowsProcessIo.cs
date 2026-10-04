using LoomLCI.Core;
using LoomLCI.Core.Processes;

namespace LoomLCI.Windows.Processes;

internal interface IWindowsProcessIo : IAsyncDisposable
{
    ProcessIoMode IoMode { get; }
    string? StdoutSpoolPath { get; }
    string? StderrSpoolPath { get; }
    string? TerminalSpoolPath { get; }

    ProcessOutputReadResult Read(
        ProcessStatusResult process,
        long stdoutCursor,
        long stderrCursor,
        long terminalCursor,
        int maxChars);

    Task<LoomResult<Unit>> WriteAsync(
        string text,
        CancellationToken cancellationToken);

    Task<LoomResult<Unit>> ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken);
}
