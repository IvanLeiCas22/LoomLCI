using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoomLCI.Core;
using LoomLCI.Core.Filesystem;

namespace LoomLCI.Windows.Filesystem;

internal sealed record FilesystemTraversalContinuation(
    long Ordinal,
    string ExpectedPath,
    FilesystemEntryType ExpectedType);

internal sealed record FilesystemSearchContinuation(
    long Ordinal,
    string ExpectedPath,
    int NextLine,
    long NextByteOffset,
    int ContextStartLine,
    long ContextStartByteOffset,
    long FileLength,
    long LastWriteTimeUtcTicks,
    string EncodingId);

internal static class FilesystemCursorCodec
{
    private const int Version = 1;
    private const int MaxCursorLength = 4096;

    private sealed record CursorPayload(
        int V,
        string O,
        string F,
        long I,
        string P,
        int T);

    private sealed record TraversalFingerprint(
        string Root,
        bool IncludeGenerated,
        IReadOnlyList<string> ExcludeDirectories,
        int MaxDepth);

    private sealed record FindFingerprint(
        string Root,
        bool IncludeGenerated,
        IReadOnlyList<string> ExcludeDirectories,
        int MaxDepth,
        IReadOnlyList<string> Queries,
        FilesystemPathMatchMode MatchMode,
        FilesystemEntryType? Type);

    private sealed record SearchFingerprint(
        string Root,
        bool IncludeGenerated,
        IReadOnlyList<string> ExcludeDirectories,
        int MaxDepth,
        IReadOnlyList<string> Queries,
        bool CaseSensitive,
        int ContextLines);

    private sealed record SearchCursorPayload(
        int V,
        string O,
        string F,
        long I,
        string P,
        int L,
        long B,
        int C,
        long D,
        long S,
        long M,
        string E);

    public static string CreateListTreeFingerprint(
        string root,
        FilesystemTraversalOptions traversal,
        int maxDepth)
        => ComputeFingerprint(new TraversalFingerprint(
            NormalizeRoot(root),
            traversal.IncludeGenerated,
            traversal.ExcludeDirectories?.ToArray() ?? [],
            maxDepth));

    public static string CreateFindPathsFingerprint(
        string root,
        IReadOnlyList<string> queries,
        FilesystemPathMatchMode matchMode,
        FilesystemEntryType? type,
        FilesystemTraversalOptions traversal,
        int maxDepth)
        => ComputeFingerprint(new FindFingerprint(
            NormalizeRoot(root),
            traversal.IncludeGenerated,
            traversal.ExcludeDirectories?.ToArray() ?? [],
            maxDepth,
            queries.ToArray(),
            matchMode,
            type));

    public static string CreateSearchTextFingerprint(
        string root,
        IReadOnlyList<string> queries,
        bool caseSensitive,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        int contextLines)
        => ComputeFingerprint(new SearchFingerprint(
            NormalizeRoot(root),
            traversal.IncludeGenerated,
            traversal.ExcludeDirectories?.ToArray() ?? [],
            maxDepth,
            queries.ToArray(),
            caseSensitive,
            contextLines));

    public static LoomResult<FilesystemTraversalContinuation?> Decode(
        string? cursor,
        string operation,
        string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return LoomResult<FilesystemTraversalContinuation?>.Success(null);
        }

        if (cursor.Length > MaxCursorLength)
        {
            return LoomResult<FilesystemTraversalContinuation?>.Failure(
                LoomErrors.InvalidArgument("cursor is too long."));
        }

        try
        {
            var payloadBytes = FromBase64Url(cursor);
            var payload = JsonSerializer.Deserialize<CursorPayload>(payloadBytes);
            if (payload is null ||
                payload.V != Version ||
                payload.I < 0 ||
                string.IsNullOrWhiteSpace(payload.P) ||
                !Enum.IsDefined(typeof(FilesystemEntryType), payload.T))
            {
                return InvalidCursor();
            }

            if (!string.Equals(payload.O, operation, StringComparison.Ordinal) ||
                !string.Equals(payload.F, fingerprint, StringComparison.Ordinal))
            {
                return LoomResult<FilesystemTraversalContinuation?>.Failure(
                    LoomErrors.InvalidArgument(
                        "cursor does not match this operation or its traversal parameters. Reuse it with the same inputs, except page size."));
            }

            return LoomResult<FilesystemTraversalContinuation?>.Success(
                new FilesystemTraversalContinuation(
                    payload.I,
                    payload.P,
                    (FilesystemEntryType)payload.T));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return InvalidCursor();
        }
    }

    public static string Encode(
        string operation,
        string fingerprint,
        FilesystemTraversalContinuation continuation)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(
            Version,
            operation,
            fingerprint,
            continuation.Ordinal,
            continuation.ExpectedPath,
            (int)continuation.ExpectedType));
        return ToBase64Url(payload);
    }

    public static LoomResult<FilesystemSearchContinuation?> DecodeSearch(
        string? cursor,
        string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return LoomResult<FilesystemSearchContinuation?>.Success(null);
        }

        if (cursor.Length > MaxCursorLength)
        {
            return LoomResult<FilesystemSearchContinuation?>.Failure(
                LoomErrors.InvalidArgument("cursor is too long."));
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SearchCursorPayload>(
                FromBase64Url(cursor));
            if (payload is null ||
                payload.V != Version ||
                !string.Equals(payload.O, "search_text", StringComparison.Ordinal) ||
                payload.I < 0 ||
                string.IsNullOrWhiteSpace(payload.P) ||
                payload.L < 1 ||
                payload.B < 0 ||
                payload.C < 1 ||
                payload.D < 0 ||
                payload.C > payload.L ||
                payload.D > payload.B ||
                payload.S < 0 ||
                payload.M < 0 ||
                string.IsNullOrWhiteSpace(payload.E))
            {
                return InvalidSearchCursor();
            }

            if (!string.Equals(payload.F, fingerprint, StringComparison.Ordinal))
            {
                return LoomResult<FilesystemSearchContinuation?>.Failure(
                    LoomErrors.InvalidArgument(
                        "cursor does not match this search or its traversal parameters. Reuse it with the same inputs, except maxResults."));
            }

            return LoomResult<FilesystemSearchContinuation?>.Success(
                new FilesystemSearchContinuation(
                    payload.I,
                    payload.P,
                    payload.L,
                    payload.B,
                    payload.C,
                    payload.D,
                    payload.S,
                    payload.M,
                    payload.E));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return InvalidSearchCursor();
        }
    }

    public static string EncodeSearch(
        string fingerprint,
        FilesystemSearchContinuation continuation)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SearchCursorPayload(
            Version,
            "search_text",
            fingerprint,
            continuation.Ordinal,
            continuation.ExpectedPath,
            continuation.NextLine,
            continuation.NextByteOffset,
            continuation.ContextStartLine,
            continuation.ContextStartByteOffset,
            continuation.FileLength,
            continuation.LastWriteTimeUtcTicks,
            continuation.EncodingId));
        return ToBase64Url(payload);
    }

    public static LoomError StaleCursorError()
        => new(
            "conflict",
            "The filesystem changed at the cursor resume point. Restart the operation without cursor.",
            false,
            new Dictionary<string, object?> { ["reason"] = "cursor_stale" });

    private static LoomResult<FilesystemTraversalContinuation?> InvalidCursor()
        => LoomResult<FilesystemTraversalContinuation?>.Failure(
            LoomErrors.InvalidArgument("cursor is malformed or unsupported."));

    private static LoomResult<FilesystemSearchContinuation?> InvalidSearchCursor()
        => LoomResult<FilesystemSearchContinuation?>.Failure(
            LoomErrors.InvalidArgument("cursor is malformed or unsupported."));

    private static string ComputeFingerprint<T>(T value)
        => ToBase64Url(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static string NormalizeRoot(string root)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid Base64Url length.")
        };
        return Convert.FromBase64String(normalized);
    }
}
