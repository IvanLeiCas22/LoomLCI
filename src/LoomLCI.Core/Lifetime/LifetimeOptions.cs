namespace LoomLCI.Core.Lifetime;

public sealed class LifetimeOptions
{
    public static readonly TimeSpan DefaultWorkSessionIdleTimeout = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan DefaultTombstoneRetention = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(30);

    public LifetimeOptions(
        TimeSpan? workSessionIdleTimeout = null,
        TimeSpan? tombstoneRetention = null,
        TimeSpan? sweepInterval = null)
    {
        WorkSessionIdleTimeout = ValidatePositive(
            workSessionIdleTimeout ?? DefaultWorkSessionIdleTimeout,
            nameof(workSessionIdleTimeout));
        TombstoneRetention = ValidatePositive(
            tombstoneRetention ?? DefaultTombstoneRetention,
            nameof(tombstoneRetention));
        SweepInterval = ValidatePositive(
            sweepInterval ?? DefaultSweepInterval,
            nameof(sweepInterval));
    }

    public TimeSpan WorkSessionIdleTimeout { get; }
    public TimeSpan TombstoneRetention { get; }
    public TimeSpan SweepInterval { get; }

    private static TimeSpan ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Lifetime values must be positive.");
        }

        return value;
    }
}
