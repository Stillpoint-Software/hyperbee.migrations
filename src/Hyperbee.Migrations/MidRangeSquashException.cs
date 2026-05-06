namespace Hyperbee.Migrations;

/// <summary>
/// Thrown by <see cref="MigrationRunner"/> when a squash migration is encountered
/// against a ledger whose state is a strict subset of the squash's <c>Replaces</c>
/// graph — i.e., the runner cannot safely auto-mark (some replaced versions are
/// missing) and cannot safely re-run the squash body (some replaced versions are
/// already applied). Per ADR-0019.
/// </summary>
/// <remarks>
/// The exception names the missing version(s) and surfaces three documented
/// recovery paths so the operator can choose the right resolution:
/// <list type="number">
///   <item>Restore the ledger from a backup that pre-dates the partial state.</item>
///   <item>Re-introduce the missing migrations from version control and let the
///         runner apply them; the squash's auto-mark path then succeeds.</item>
///   <item>Run <c>dotnet hyperbee-migrations recover from-mid-range</c> (Phase 8)
///         with a deterministic acknowledgement token to forcibly mark the squash
///         as applied without running its body — only safe when the operator has
///         externally verified that the live data state already matches the
///         squashed schema.</item>
/// </list>
/// </remarks>
[Serializable]
public class MidRangeSquashException : MigrationException
{
    public long SquashVersion { get; init; }
    public long[] MissingVersions { get; init; } = Array.Empty<long>();
    public long[] AppliedVersions { get; init; } = Array.Empty<long>();

    public MidRangeSquashException()
    : base( "Mid-range squash state detected." )
    {
    }

    public MidRangeSquashException( string message )
    : base( message )
    {
    }

    public MidRangeSquashException(
        long squashVersion,
        IEnumerable<long> missingVersions,
        IEnumerable<long> appliedVersions )
    : base( BuildMessage( squashVersion, missingVersions?.ToArray() ?? Array.Empty<long>(), appliedVersions?.ToArray() ?? Array.Empty<long>() ) )
    {
        SquashVersion = squashVersion;
        MissingVersions = missingVersions?.ToArray() ?? Array.Empty<long>();
        AppliedVersions = appliedVersions?.ToArray() ?? Array.Empty<long>();
    }

    private static string BuildMessage( long squashVersion, long[] missing, long[] applied )
    {
        var missingText = missing.Length > 0 ? string.Join( ", ", missing ) : "<none>";
        var appliedText = applied.Length > 0 ? string.Join( ", ", applied ) : "<none>";

        return
            $"Squash migration version {squashVersion} cannot run: the ledger is in a mid-range state. " +
            $"Missing versions [{missingText}]; already-applied versions [{appliedText}]. " +
            "Recovery paths: " +
            "(1) restore the ledger from a backup that pre-dates the partial state; " +
            "(2) re-introduce the missing migrations from version control and re-run; " +
            "(3) `dotnet hyperbee-migrations recover from-mid-range` with a deterministic " +
            "acknowledgement token (only when the live data state has been externally verified " +
            "to match the squashed schema). See ADR-0019.";
    }
}
