namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Two-phase fleet readiness gate per ADR-0019 + Phase 7 Task 7.4. Static
/// validation entry points so callers (runner deploy path, CLI generation
/// path) share one rule set without taking a runtime dependency on each
/// other's wiring.
/// </summary>
public static class SquashFleetGate
{
    /// <summary>
    /// Deploy-time check: refuse the squash on this environment if either
    /// (a) the env isn't in <see cref="SquashMetadata.ExpectedFleetVersions"/>,
    /// or (b) the env is below its recorded minimum AND the squash's
    /// staleness window has elapsed.
    /// </summary>
    /// <param name="metadata">Squash's sidecar metadata.</param>
    /// <param name="environmentName">Live environment name (matches manifest entries).</param>
    /// <param name="environmentLastAppliedVersion">
    /// Highest pre-squash version the live ledger has recorded. Pass
    /// <c>0</c> when the ledger is empty.
    /// </param>
    /// <param name="now">Current instant (caller-supplied for testability).</param>
    /// <exception cref="UnregisteredEnvironmentException">
    /// Thrown when <paramref name="environmentName"/> is not in
    /// <see cref="SquashMetadata.ExpectedFleetVersions"/>.
    /// </exception>
    /// <exception cref="StaleFleetMemberException">
    /// Thrown when the env is below its recorded minimum AND the staleness
    /// window has elapsed.
    /// </exception>
    public static void EnsureDeployable(
        SquashMetadata metadata,
        string environmentName,
        long environmentLastAppliedVersion,
        DateTimeOffset now )
    {
        ArgumentNullException.ThrowIfNull( metadata );
        if ( string.IsNullOrWhiteSpace( environmentName ) )
            throw new ArgumentException( "environmentName is required.", nameof( environmentName ) );

        if ( !metadata.ExpectedFleetVersions.TryGetValue( environmentName, out var expectedMin ) )
        {
            throw new UnregisteredEnvironmentException(
                environmentName,
                metadata.ExpectedFleetVersions.Keys );
        }

        if ( environmentLastAppliedVersion >= expectedMin )
            return; // env is at or past the required minimum — deploy is safe.

        // Environment is below the minimum. The deploy is allowed only if the
        // squash is still inside its staleness window — gives the operator
        // grace to bring the env forward without re-generating.
        var elapsed = now - metadata.GeneratedAt;
        if ( elapsed <= metadata.MaxStalenessWindow )
            return;

        throw new StaleFleetMemberException(
            environmentName,
            expectedMin,
            environmentLastAppliedVersion,
            elapsed,
            metadata.MaxStalenessWindow );
    }

    /// <summary>
    /// Generation-time check: refuse to generate the squash if any registered
    /// fleet member's last-applied version is mid-range with respect to the
    /// proposed squash range. Per A2 (two-phase fleet readiness gate).
    /// </summary>
    /// <param name="proposedReplacesFromVersion">Inclusive low end of the squash range.</param>
    /// <param name="proposedReplacesToVersion">Inclusive high end of the squash range.</param>
    /// <param name="fleetMembers">
    /// Per-environment last-applied state. Members below the low bound are fine
    /// (they'll auto-mark or run the squash body); members at-or-above the high
    /// bound are fine (they're already past the squash); members strictly inside
    /// the range are mid-range and refused.
    /// </param>
    /// <exception cref="MidRangeFleetException">
    /// Thrown when one or more fleet members are mid-range.
    /// </exception>
    public static void EnsureGenerable(
        long proposedReplacesFromVersion,
        long proposedReplacesToVersion,
        IEnumerable<FleetMemberState> fleetMembers )
    {
        ArgumentNullException.ThrowIfNull( fleetMembers );

        if ( proposedReplacesToVersion < proposedReplacesFromVersion )
            throw new ArgumentException(
                $"to-version ({proposedReplacesToVersion}) is less than from-version ({proposedReplacesFromVersion}).",
                nameof( proposedReplacesToVersion ) );

        var offenders = new List<MidRangeFleetMember>();
        foreach ( var m in fleetMembers )
        {
            // Member is mid-range if it has applied SOME but not ALL of the
            // squash's replaced versions — i.e., last-applied is at-or-above
            // the low bound but strictly below the high bound.
            if ( m.LastAppliedVersion >= proposedReplacesFromVersion
                 && m.LastAppliedVersion < proposedReplacesToVersion )
            {
                offenders.Add( new MidRangeFleetMember
                {
                    EnvironmentName = m.EnvironmentName,
                    LastAppliedVersion = m.LastAppliedVersion,
                    FirstMissingVersion = m.LastAppliedVersion + 1
                } );
            }
        }

        if ( offenders.Count > 0 )
            throw new MidRangeFleetException( offenders );
    }
}

/// <summary>
/// Per-environment state probed by the squash CLI's fleet readiness check
/// (per Task 7.3) — the env name plus the highest version present in its
/// ledger. Members strictly inside the proposed squash range trigger
/// <see cref="MidRangeFleetException"/>.
/// </summary>
public sealed record FleetMemberState(
    string EnvironmentName,
    long LastAppliedVersion );
