using System.Threading.Channels;
using LoomLCI.Core.Observability;
using Microsoft.Extensions.Hosting;

namespace LoomLCI.Host;

/// <summary>Independent, nonblocking diagnostic path; does not drain the regular event bus.</summary>
public sealed class LoomDiagnosticsService : BackgroundService
{
    private readonly LoomEventBus _events;
    private readonly string _dataRoot;
    private readonly Channel<DiagnosticRecord> _queue =
        Channel.CreateBounded<DiagnosticRecord>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private long _dropped;

    public LoomDiagnosticsService(LoomEventBus events)
    {
        _events = events;
        _dataRoot = Environment.GetEnvironmentVariable("LOOMLCI_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "LoomLCI", "deployment");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _events.SetDiagnosticsObserver(Offer);
        try
        {
            await foreach (var record in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (!DiagnosticsLog.IsEnabled(_dataRoot))
                {
                    Interlocked.Exchange(ref _dropped, 0);
                    continue;
                }

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    DiagnosticsLog.TryAppend(_dataRoot,
                        new DiagnosticRecord(DateTimeOffset.UtcNow, "host", "EventsDropped", Count: dropped));
                DiagnosticsLog.TryAppend(_dataRoot, record);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _events.SetDiagnosticsObserver(null);
        }
    }

    private void Offer(LoomEvent evt)
    {
        var record = DiagnosticEventProjector.Project(evt);
        if (record is not null && !_queue.Writer.TryWrite(record))
            Interlocked.Increment(ref _dropped);
    }


}
