using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Work;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LoomLCI.Host;

public sealed class LifetimeSweeperService : BackgroundService
{
    private readonly WorkSessionManager _workSessions;
    private readonly ProcessCapability _processes;
    private readonly LifetimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LifetimeSweeperService> _logger;

    public LifetimeSweeperService(
        WorkSessionManager workSessions,
        ProcessCapability processes,
        LifetimeOptions options,
        TimeProvider timeProvider,
        ILogger<LifetimeSweeperService> logger)
    {
        _workSessions = workSessions;
        _processes = processes;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            _options.SweepInterval,
            _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)
                       .ConfigureAwait(false))
            {
                try
                {
                    var processResult = await _processes.SweepExpiredAsync()
                        .ConfigureAwait(false);
                    var workResult = await _workSessions.SweepExpiredAsync()
                        .ConfigureAwait(false);

                    if (workResult.FailedCleanups > 0)
                    {
                        _logger.LogWarning(
                            "Lifetime sweep found {FailedCleanups} incomplete work session cleanup(s); they remain retryable.",
                            workResult.FailedCleanups);
                    }

                    if (processResult.ExpiredProcesses != 0 ||
                        workResult.ExpiredSessions != 0 ||
                        workResult.PrunedSessions != 0 ||
                        workResult.PrunedResources != 0)
                    {
                        _logger.LogDebug(
                            "Lifetime sweep: expiredProcesses={ExpiredProcesses}, expiredSessions={ExpiredSessions}, prunedSessions={PrunedSessions}, prunedResources={PrunedResources}.",
                            processResult.ExpiredProcesses,
                            workResult.ExpiredSessions,
                            workResult.PrunedSessions,
                            workResult.PrunedResources);
                    }
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException ||
                    !stoppingToken.IsCancellationRequested)
                {
                    // A faulty disposer must not permanently stop future sweeps.
                    _logger.LogError(ex, "Lifetime sweep failed; retrying on the next interval.");
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
