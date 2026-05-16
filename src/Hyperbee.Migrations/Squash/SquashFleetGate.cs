namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Generation-time fleet readiness gate per ADR-0019 A2. Static
/// validation entry point so the CLI generation path enforces the rule
/// without a runtime dependency on runner wiring.
/// </summary>
/// <remarks>
/// The deploy-time half (<c>EnsureDeployable</c> +
/// <c>StaleFleetMemberException</c> + <c>UnregisteredEnvironmentException</c>)
/// was cut per ADR-0026: it was never wired, and the silent-stranding
/// failure it targeted is already converted to a loud, recoverable
/// apply-time refusal by the wired <c>MigrationRunner</c>
/// <c>MidRangeSquashException</c> reconciliation path. Only the
/// generation-time <see cref="EnsureGenerable"/> remains.
/// </remarks>
public static class SquashFleetGate
{
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
