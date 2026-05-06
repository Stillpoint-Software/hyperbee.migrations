namespace Hyperbee.Migrations;

/// <summary>
/// Thrown by record stores when a ledger row violates the
/// <c>Kind</c>/<c>Replaces</c> consistency rule established by ADR-0021 (A1):
/// <c>Kind == Squash</c> requires a non-empty <c>Replaces</c> set;
/// <c>Kind == Migration</c> requires an empty <c>Replaces</c> set.
/// </summary>
/// <remarks>
/// Raised at both write time (refusing to commit an inconsistent row) and read
/// time (refusing to surface an inconsistent row to the runner). Indicates
/// either operator tampering or a code bug; not retryable.
/// </remarks>
[Serializable]
public class MigrationLedgerIntegrityException : MigrationException
{
    public string RecordId { get; init; }

    public MigrationLedgerIntegrityException()
    : base( "Migration ledger integrity violation." )
    {
    }

    public MigrationLedgerIntegrityException( string message )
    : base( message )
    {
    }

    public MigrationLedgerIntegrityException( string message, string recordId )
    : base( message )
    {
        RecordId = recordId;
    }

    public MigrationLedgerIntegrityException( string message, Exception innerException )
    : base( message, innerException )
    {
    }
}
