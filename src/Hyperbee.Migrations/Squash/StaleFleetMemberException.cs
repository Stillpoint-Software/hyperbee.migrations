namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Thrown by the runner at deploy time when a squash's
/// <see cref="SquashMetadata.ExpectedFleetVersions"/> entry for the live
/// environment names a minimum version that the live environment hasn't
/// reached AND the squash's <see cref="SquashMetadata.MaxStalenessWindow"/>
/// has elapsed (per ADR-0019 A2 + A15).
/// </summary>
/// <remarks>
/// <para>
/// This is the deploy-time half of the two-phase fleet readiness gate. The
/// generation-time half (<see cref="MidRangeFleetException"/>) refuses
/// generation when fleet members are mid-range; this one refuses *deploy*
/// when a registered fleet member has gone too long without applying the
/// pre-squash migrations (the squash's auto-mark would be wrong if applied
/// in this state).
/// </para>
/// </remarks>
[Serializable]
public class StaleFleetMemberException : MigrationException
{
    public string EnvironmentName { get; init; }
    public long ExpectedMinVersion { get; init; }
    public long ActualVersion { get; init; }
    public TimeSpan StalenessElapsed { get; init; }
    public TimeSpan MaxStalenessWindow { get; init; }

    public StaleFleetMemberException()
    : base( "Fleet member is stale; squash deploy refused." )
    {
    }

    public StaleFleetMemberException( string message )
    : base( message )
    {
    }

    public StaleFleetMemberException(
        string environmentName,
        long expectedMinVersion,
        long actualVersion,
        TimeSpan stalenessElapsed,
        TimeSpan maxStalenessWindow )
    : base( BuildMessage( environmentName, expectedMinVersion, actualVersion, stalenessElapsed, maxStalenessWindow ) )
    {
        EnvironmentName = environmentName;
        ExpectedMinVersion = expectedMinVersion;
        ActualVersion = actualVersion;
        StalenessElapsed = stalenessElapsed;
        MaxStalenessWindow = maxStalenessWindow;
    }

    private static string BuildMessage(
        string env, long expected, long actual, TimeSpan elapsed, TimeSpan max )
    {
        return
            $"Environment `{env}` is at version {actual} but the squash requires it to be at >= {expected}. " +
            $"The squash was generated {elapsed.TotalDays:F1} days ago which exceeds the max-staleness-window " +
            $"of {max.TotalDays:F1} days. Per ADR-0019 the deploy is refused: bring the environment forward " +
            $"to the minimum version (run prior migrations) or regenerate the squash with the current fleet state.";
    }
}
