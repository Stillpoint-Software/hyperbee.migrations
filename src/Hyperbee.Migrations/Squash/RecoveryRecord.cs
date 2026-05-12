using System.Globalization;
using System.Text;

namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Helpers for constructing the persisted mid-range recovery acknowledgement
/// row written by <c>hyperbee-migrations recover from-mid-range</c> and read
/// by <see cref="MigrationRunner"/> on next invocation (per ADR-0019 A3 +
/// ADR-0024 audit RB-2). The row is a regular <see cref="MigrationRecord"/>
/// with <see cref="MigrationRecordKind.Recovery"/>; the row's
/// <see cref="MigrationRecord.Replaces"/> carries the missing-versions list
/// and <see cref="MigrationRecord.Checksum"/> carries the deterministic
/// acknowledgement token. The runner re-verifies the token before
/// force-marking the squash and deletes the row once the squash is
/// auto-marked, so a stale acknowledgement cannot force-mark a second time.
/// </summary>
public static class RecoveryRecord
{
    /// <summary>
    /// Deterministic record id for a recovery acknowledgement. Same id is
    /// produced from the same (squashVersion, environmentName) pair so the
    /// CLI write and the runner read agree without coordination. Environment
    /// name is slug-encoded so any characters that some providers reject in
    /// id strings cannot appear (lowercased, non-alphanumeric replaced with
    /// '_'; empty names map to '<![CDATA[<unset>]]>').
    /// </summary>
    public static string IdFor( long squashVersion, string environmentName )
    {
        var slug = SlugEnvironment( environmentName );
        return string.Create(
            CultureInfo.InvariantCulture,
            $"recovery.from-mid-range.{squashVersion}.{slug}" );
    }

    /// <summary>
    /// Builds the recovery <see cref="MigrationRecord"/>. The caller writes it
    /// via <see cref="IMigrationRecordStore.WriteAsync(MigrationRecord, WritePrecondition, System.Threading.CancellationToken)"/>.
    /// Returns null when <paramref name="environmentName"/> is null/whitespace --
    /// callers should require an explicit environment name to derive an
    /// unambiguous row identity.
    /// </summary>
    /// <param name="squashVersion">Version of the squash being recovered.</param>
    /// <param name="environmentName">Environment name supplied to the CLI. Required.</param>
    /// <param name="missingVersions">Missing-version list from the runner's
    /// classification; the row's <see cref="MigrationRecord.Replaces"/> field
    /// carries it for token re-verification.</param>
    public static MigrationRecord Build(
        long squashVersion,
        string environmentName,
        IEnumerable<long> missingVersions )
    {
        if ( string.IsNullOrWhiteSpace( environmentName ) )
            throw new ArgumentException( "environmentName is required.", nameof( environmentName ) );
        if ( missingVersions == null )
            throw new ArgumentNullException( nameof( missingVersions ) );

        var sortedMissing = missingVersions
            .Distinct()
            .OrderBy( v => v )
            .ToArray();

        if ( sortedMissing.Length == 0 )
            throw new ArgumentException(
                "missingVersions must contain at least one version; a recovery row " +
                "with an empty Replaces set is rejected by EnsureLedgerIntegrity.",
                nameof( missingVersions ) );

        var token = RecoveryAcknowledgement.ComputeToken( environmentName, squashVersion, sortedMissing );

        return new MigrationRecord
        {
            Id = IdFor( squashVersion, environmentName ),
            Kind = MigrationRecordKind.Recovery,
            Replaces = sortedMissing,
            Checksum = token,
            RunOn = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Verifies that <paramref name="recoveryRow"/> is still valid for
    /// force-marking the given squash against the current
    /// <paramref name="missing"/> set: the persisted token must match a fresh
    /// recompute, and the row's <see cref="MigrationRecord.Replaces"/> must be
    /// the same multiset as <paramref name="missing"/>. Returns false on any
    /// mismatch (stale recovery row from a previous incident, env-name typo,
    /// missing-set drift) so the runner falls through to
    /// <see cref="MidRangeSquashException"/>.
    /// </summary>
    public static bool IsValidFor(
        MigrationRecord recoveryRow,
        long squashVersion,
        string environmentName,
        IReadOnlyCollection<long> missing )
    {
        if ( recoveryRow == null )
            return false;
        if ( recoveryRow.Kind != MigrationRecordKind.Recovery )
            return false;
        if ( recoveryRow.Replaces == null || recoveryRow.Replaces.Length != missing.Count )
            return false;

        var persistedSet = new HashSet<long>( recoveryRow.Replaces );
        if ( !persistedSet.SetEquals( missing ) )
            return false;

        return RecoveryAcknowledgement.Verify( environmentName, squashVersion, missing, recoveryRow.Checksum );
    }

    private static string SlugEnvironment( string environmentName )
    {
        if ( string.IsNullOrWhiteSpace( environmentName ) )
            return "_unset_";

        var sb = new StringBuilder( environmentName.Length );
        foreach ( var c in environmentName.Trim().ToLowerInvariant() )
        {
            sb.Append( char.IsLetterOrDigit( c ) ? c : '_' );
        }
        return sb.ToString();
    }
}
