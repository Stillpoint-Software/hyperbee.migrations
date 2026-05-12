using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations;

/// <summary>
/// Thrown by <see cref="MigrationRunner"/> when a squash migration is encountered
/// against a ledger whose state is a strict subset of the squash's <c>Replaces</c>
/// graph -- i.e., the runner cannot safely auto-mark (some replaced versions are
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
///         with the deterministic acknowledgement token printed in
///         <see cref="RecoveryToken"/> to forcibly mark the squash as applied
///         without running its body -- only safe when the operator has externally
///         verified that the live data state already matches the squashed
///         schema.</item>
/// </list>
/// Per R-10 the recovery token is computed and embedded in the exception message
/// so operators have it on hand during incident response; per
/// <see cref="RecoveryAcknowledgement"/> the token is not a secret -- it is an
/// anti-typo / anti-cross-env-paste guard, not an authentication factor.
/// </remarks>
[Serializable]
public class MidRangeSquashException : MigrationException
{
    public long SquashVersion { get; init; }
    public long[] MissingVersions { get; init; } = Array.Empty<long>();
    public long[] AppliedVersions { get; init; } = Array.Empty<long>();

    /// <summary>
    /// Environment name the runner was acting against when the exception was
    /// raised (from <see cref="MigrationOptions.EnvironmentName"/>). Forms part
    /// of the <see cref="RecoveryToken"/> derivation so a token computed for one
    /// environment cannot be accidentally pasted into another.
    /// </summary>
    public string EnvironmentName { get; init; }

    /// <summary>
    /// Deterministic acknowledgement token derived from
    /// <c>(EnvironmentName, SquashVersion, MissingVersions)</c>. Operators pass
    /// this verbatim to <c>recover from-mid-range</c>. The token is reproducible:
    /// retrying with the same inputs yields the same token. Not a secret.
    /// </summary>
    public string RecoveryToken { get; init; }

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
    : this( squashVersion, missingVersions, appliedVersions, environmentName: null )
    {
    }

    public MidRangeSquashException(
        long squashVersion,
        IEnumerable<long> missingVersions,
        IEnumerable<long> appliedVersions,
        string environmentName )
    : base( BuildMessage(
            squashVersion,
            missingVersions?.ToArray() ?? Array.Empty<long>(),
            appliedVersions?.ToArray() ?? Array.Empty<long>(),
            environmentName,
            out var token ) )
    {
        SquashVersion = squashVersion;
        MissingVersions = missingVersions?.ToArray() ?? Array.Empty<long>();
        AppliedVersions = appliedVersions?.ToArray() ?? Array.Empty<long>();
        EnvironmentName = environmentName;
        RecoveryToken = token;
    }

    private static string BuildMessage(
        long squashVersion,
        long[] missing,
        long[] applied,
        string environmentName,
        out string token )
    {
        var missingText = missing.Length > 0 ? string.Join( ", ", missing ) : "<none>";
        var appliedText = applied.Length > 0 ? string.Join( ", ", applied ) : "<none>";

        // Compute the recovery acknowledgement token. The token derivation
        // requires a non-empty environment name; substitute a sentinel when
        // none is supplied so the operator still gets *a* token plus a
        // remediation note about setting MigrationOptions.EnvironmentName.
        // The sentinel-derived token is still deterministic but won't match
        // a fleet-manifest-supplied env name, which is the point: it forces
        // the operator to specify the env before running recover.
        var envForToken = string.IsNullOrWhiteSpace( environmentName )
            ? "<unset>"
            : environmentName;
        token = RecoveryAcknowledgement.ComputeToken( envForToken, squashVersion, missing );

        var envBlurb = string.IsNullOrWhiteSpace( environmentName )
            ? " (NOTE: MigrationOptions.EnvironmentName was not set; the token above " +
              "is derived against '<unset>'. Set EnvironmentName explicitly before " +
              "running recover or the token will not match the fleet-manifest identity.)"
            : $" (environment: {environmentName})";

        return
            $"Squash migration version {squashVersion} cannot run: the ledger is in a mid-range state. " +
            $"Missing versions [{missingText}]; already-applied versions [{appliedText}]. " +
            "Recovery paths: " +
            "(1) restore the ledger from a backup that pre-dates the partial state; " +
            "(2) re-introduce the missing migrations from version control and re-run; " +
            "(3) `dotnet hyperbee-migrations recover from-mid-range --token " + token + "`" + envBlurb +
            " (only when the live data state has been externally verified to match " +
            "the squashed schema). See ADR-0019.";
    }
}
