using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Work;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LoomLCI.Host;

public sealed class LifetimeSweeperService : BackgroundService
{
    private readonly WorkSessionManager _workSessions;
    private readonly LifetimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LifetimeSweeperService> _logger;

    public LifetimeSweeperService(
        WorkSessionManager workSessions,
        LifetimeOptions options,
        TimeProvider timeProvider,
        ILogger<LifetimeSweeperService> logger)
    {
        _workSessions = workSessions;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var result = await _workSessions.SweepExpiredAsync().ConfigureAwait(false);

                if (result.ExpiredSessions != 0 ||
                    result.PrunedSessions != 0 ||
                    result.PrunedResources != 0)
                {
                    _logger.LogDebug(
                        "Lifetime sweep: expiredSessions={ExpiredSessions}, prunedSessions={PrunedSessions}, prunedResources={PrunedResources}.",
                        result.ExpiredSessions,
                        result.PrunedSessions,
                        result.PrunedResources);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
