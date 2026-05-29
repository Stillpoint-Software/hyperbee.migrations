namespace Hyperbee.Migrations;

/// <summary>
/// Thrown by <see cref="MigrationRunner"/> at startup when a previously
/// interrupted migration is detected: an in-flight sentinel row (ADR-0027) for
/// a <c>[DataMigration]</c> (or unannotated) migration survives without a
/// corresponding journal row, meaning the migration body was interrupted
/// mid-run (SIGTERM / SIGKILL / node death) and never recorded as applied.
/// </summary>
/// <remarks>
/// The runner fails closed rather than silently re-running, because re-running
/// a partially-applied non-idempotent data migration would double-apply the
/// statements that committed before the interruption. The operator must verify
/// the live data state, then either reconcile it or set
/// <see cref="MigrationOptions.ForceResume"/> to reap the sentinel and re-run.
/// Migrations marked <c>[StructuralOnly]</c> do not raise this exception -- their
/// replay is idempotent, so the sentinel is reaped and the migration re-runs.
/// </remarks>
[Serializable]
public class MigrationInterruptedException : MigrationException
{
    /// <summary>Record id of the interrupted migration (not the sentinel id).</summary>
    public string RecordId { get; init; }

    public MigrationInterruptedException()
    : base( "A previously interrupted migration was detected." )
    {
    }

    public MigrationInterruptedException( string message )
    : base( message )
    {
    }

    public MigrationInterruptedException( string recordId, long version, string name )
    : base( BuildMessage( recordId, version, name ) )
    {
        RecordId = recordId;
    }

    private static string BuildMessage( string recordId, long version, string name ) =>
        $"Migration [{version}] {name} (`{recordId}`) was interrupted mid-run on a previous " +
        "invocation: an in-flight sentinel survives with no journal row, so the migration body " +
        "started but never completed. The runner refuses to silently re-run it because a " +
        "partially-applied data migration would double-apply on replay. " +
        "Resolve by verifying the live data state, then either reconcile it manually or set " +
        "Migrations:ForceResume = true to reap the sentinel and re-run. " +
        "(Migrations marked [StructuralOnly] re-run automatically; this lockout applies to " +
        "[DataMigration] and unannotated migrations.) See ADR-0027.";
}
