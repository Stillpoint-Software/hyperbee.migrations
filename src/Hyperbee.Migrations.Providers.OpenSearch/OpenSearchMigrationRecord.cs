#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch;

// R-06 forensic ledger record. Extends the base MigrationRecord with the
// fields the OpenSearch ledger schema declares and that R-19 needs to drive
// partial-rollback recovery.
//
// Schema fields (per LedgerIndexInitStep strict mapping):
//   id                   - keyword
//   runOn                - date
//   direction            - keyword  ("Up" | "Down")
//   status               - keyword  ("succeeded" | "failed" | "partially_rolled_back")
//   appliedBy            - keyword  ({machineName}/{processId}[/{RunnerId}])
//   checksum             - keyword  (content hash; deferred — Slice 2.5 leaves null)
//   error                - text
//   failedStatementIndex - integer  (nullable; populated only for partial rollback)

public class OpenSearchMigrationRecord : MigrationRecord
{
    /// <summary>Canonical status keyword: a successfully-applied migration.</summary>
    public const string StatusSucceeded = "succeeded";

    /// <summary>Canonical status keyword: a failed migration (Up direction).</summary>
    public const string StatusFailed = "failed";

    /// <summary>
    /// Canonical status keyword: a Down sequence halted partway through. Per R-19, subsequent runs
    /// in either direction are refused unless OpenSearchMigrationOptions.ForceResume is set.
    /// </summary>
    public const string StatusPartiallyRolledBack = "partially_rolled_back";

    /// <summary>
    /// The direction this record was written for: "Up" on a successful UpAsync,
    /// "Down" on a successful (full) rollback record overwrite.
    /// </summary>
    public string? Direction { get; init; }

    /// <summary>
    /// One of "succeeded", "failed", "partially_rolled_back". A successful Up
    /// completes with "succeeded"; a partial rollback with
    /// "partially_rolled_back" plus a non-null FailedStatementIndex.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Runner identity for forensic attribution: "{machineName}/{processId}".
    /// </summary>
    public string? AppliedBy { get; init; }

    /// <summary>
    /// Content checksum (statement-set hash). Deferred to a follow-up slice;
    /// always null in the current implementation.
    /// </summary>
    public string? Checksum { get; init; }

    /// <summary>
    /// Error detail when Status is "failed" or "partially_rolled_back".
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Index of the rollback statement that failed (R-19); null unless
    /// Status is "partially_rolled_back".
    /// </summary>
    public int? FailedStatementIndex { get; init; }
}
