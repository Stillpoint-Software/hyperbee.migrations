using System;

namespace Hyperbee.Migrations;

/// <summary>
/// Helpers for the in-flight sentinel row written immediately before a
/// migration's body runs and deleted once the migration's real journal row is
/// committed (per ADR-0027). The sentinel is a regular
/// <see cref="MigrationRecord"/> with <see cref="MigrationRecordKind.InProgress"/>
/// living at a derived id, so the applied-set (which is keyed on real migration
/// record ids) never observes it as "applied". A sentinel surviving into the
/// next run is the durable signal that its migration was interrupted mid-body.
/// </summary>
public static class InProgressRecord
{
    // Prefix chosen so a sentinel id can never collide with a real migration
    // record id (which begins with the numeric version per ADR-0009) nor with a
    // recovery row id (which begins with "recovery."). The marker is matched by
    // exact derived id, not by scanning a prefix, but the prefix keeps the id
    // self-describing in ledger dumps.
    private const string Prefix = "inflight.";

    /// <summary>
    /// Deterministic sentinel id for a migration's record id. The same input
    /// always yields the same sentinel id, so the pre-run write and the
    /// restart pre-scan agree without coordination.
    /// </summary>
    public static string IdFor( string recordId )
    {
        ArgumentException.ThrowIfNullOrEmpty( recordId );
        return Prefix + recordId;
    }

    /// <summary>
    /// Builds the in-flight sentinel <see cref="MigrationRecord"/> for the given
    /// migration record id. The caller writes it via
    /// <see cref="IMigrationRecordStore.WriteAsync(MigrationRecord, WritePrecondition, System.Threading.CancellationToken)"/>
    /// before running the migration body, and deletes it (by
    /// <see cref="IdFor(string)"/>) after the real journal row commits.
    /// </summary>
    public static MigrationRecord Build( string recordId )
    {
        ArgumentException.ThrowIfNullOrEmpty( recordId );

        return new MigrationRecord
        {
            Id = IdFor( recordId ),
            Kind = MigrationRecordKind.InProgress,
            RunOn = DateTimeOffset.UtcNow
        };
    }
}
