namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Thrown by the squash CLI's fleet readiness check (per ADR-0019 + Phase 7
/// Task 7.3) at GENERATION time when one or more registered fleet members are
/// in a mid-range state — i.e., the env's last-applied version falls inside
/// the squash range but doesn't reach the upper bound. Per ADR-0019 A2
/// (two-phase fleet gate) the squash refuses to generate in this state to
/// prevent shipping a squash that some members can't auto-mark safely.
/// </summary>
/// <remarks>
/// Generation-time fleet readiness gate (ADR-0019 A2). The deploy-time
/// half was cut per ADR-0026; the equivalent loud, recoverable
/// apply-time refusal for a mid-range environment is
/// <c>MidRangeSquashException</c> raised by the wired
/// <c>MigrationRunner</c> reconciliation path.
/// </remarks>
[Serializable]
public class MidRangeFleetException : MigrationException
{
    public IReadOnlyList<MidRangeFleetMember> OffendingEnvironments { get; init; } = Array.Empty<MidRangeFleetMember>();

    public MidRangeFleetException()
    : base( "Fleet has mid-range members; squash generation refused." )
    {
    }

    public MidRangeFleetException( string message )
    : base( message )
    {
    }

    public MidRangeFleetException( IEnumerable<MidRangeFleetMember> offenders )
    : base( BuildMessage( offenders?.ToArray() ?? Array.Empty<MidRangeFleetMember>() ) )
    {
        OffendingEnvironments = offenders?.ToArray() ?? Array.Empty<MidRangeFleetMember>();
    }

    private static string BuildMessage( MidRangeFleetMember[] offenders )
    {
        if ( offenders.Length == 0 )
            return "Fleet has mid-range members; squash generation refused.";

        var detail = string.Join(
            "; ",
            offenders.Select( o =>
                $"{o.EnvironmentName} last-applied={o.LastAppliedVersion} first-missing={o.FirstMissingVersion}" ) );

        return
            $"Squash generation refused: {offenders.Length} fleet member(s) are mid-range. {detail}. " +
            "Per ADR-0019 A2 (two-phase fleet readiness gate) the squash CLI refuses to generate while any " +
            "registered environment hasn't reached the squash's upper bound. " +
            "Recovery: bring the listed environments forward by applying the missing migrations, " +
            "or remove them from the fleet manifest if intentionally stranded.";
    }
}

/// <summary>
/// Per-environment offender record for a fleet readiness mid-range refusal.
/// </summary>
public sealed record MidRangeFleetMember
{
    public required string EnvironmentName { get; init; }
    public required long LastAppliedVersion { get; init; }
    public required long FirstMissingVersion { get; init; }
}
