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

    public bool Publish(
        string kind,
        string source,
        WorkId? workId = null,
        InvocationId? invocationId = null,
        ResourceHandle? resourceHandle = null,
        IReadOnlyDictionary<string, object?>? payload = null)
        => _channel.Writer.TryWrite(new LoomEvent(
            IdentifierFactory.Create("evt"),
            DateTimeOffset.UtcNow,
            kind,
            source,
            workId,
            invocationId,
            resourceHandle,
            payload));

    public bool TryRead(out LoomEvent? loomEvent)
        => _channel.Reader.TryRead(out loomEvent);

    public IAsyncEnumerable<LoomEvent> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
