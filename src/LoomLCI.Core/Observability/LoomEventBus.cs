using System.Threading.Channels;

namespace LoomLCI.Core.Observability;

public sealed record LoomEvent(
    string EventId,
    DateTimeOffset Timestamp,
    string Kind,
    string Source,
    WorkId? WorkId = null,
    InvocationId? InvocationId = null,
    ResourceHandle? ResourceHandle = null,
    IReadOnlyDictionary<string, object?>? Payload = null);

public sealed class LoomEventBus
{
    private readonly Channel<LoomEvent> _channel = Channel.CreateBounded<LoomEvent>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

    private Action<LoomEvent>? _diagnosticsObserver;

    // Optional secondary observer: never steals events from existing readers.
    public void SetDiagnosticsObserver(Action<LoomEvent>? observer) =>
        Volatile.Write(ref _diagnosticsObserver, observer);

    private readonly TimeProvider _timeProvider;

    public LoomEventBus(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool Publish(
        string kind,
        string source,
        WorkId? workId = null,
        InvocationId? invocationId = null,
        ResourceHandle? resourceHandle = null,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        var evt = new LoomEvent(IdentifierFactory.Create("evt"),
            _timeProvider.GetUtcNow(), kind, source, workId, invocationId, resourceHandle, payload);
        var written = _channel.Writer.TryWrite(evt);
        try
        {
            Volatile.Read(ref _diagnosticsObserver)?.Invoke(evt);
        }
        catch
        {
            // Diagnostics are strictly best-effort and cannot affect the regular bus.
        }
        return written;
    }

    public bool TryRead(out LoomEvent? loomEvent)
        => _channel.Reader.TryRead(out loomEvent);

    public IAsyncEnumerable<LoomEvent> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
