using System;
using System.Collections.Generic;

namespace Hyperbee.Migrations;

public class MigrationRecord : IMigrationRecord
{
    public string Id { get; init; }
    public DateTimeOffset RunOn { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// SHA-256 (or other deterministic) digest of the migration's resource bytes
    /// or definition. Null on rows written by Hyperbee.Migrations v2.x.
    /// </summary>
    public string Checksum { get; init; }

    /// <summary>
    /// Migration kind. Defaults to <see cref="MigrationRecordKind.Migration"/>
    /// for pre-checksum-era rows and for the legacy
    /// <see cref="IMigrationRecordStore.WriteAsync(string)"/> overload.
    /// </summary>
    public MigrationRecordKind Kind { get; init; } = MigrationRecordKind.Migration;

    /// <summary>
    /// Versions this squash subsumes. Must be empty when
    /// <see cref="Kind"/> is <see cref="MigrationRecordKind.Migration"/> and
    /// non-empty when <see cref="Kind"/> is <see cref="MigrationRecordKind.Squash"/>.
    /// </summary>
    /// <remarks>
    /// Concrete type is <c>long[]</c> for clean MongoDB / Couchbase / OpenSearch JSON
    /// serialization; satisfies the <see cref="IMigrationRecord.Replaces"/>
    /// <see cref="IReadOnlyList{T}"/> contract.
    /// </remarks>
    public long[] Replaces { get; init; } = Array.Empty<long>();

    IReadOnlyList<long> IMigrationRecord.Replaces => Replaces;

    /// <summary>
    /// Validates the Kind/Replaces consistency rule (ADR-0021 A1). Called by
    /// record stores at write and read time; raises
    /// <see cref="MigrationLedgerIntegrityException"/> on violation.
    /// </summary>
    public void EnsureLedgerIntegrity()
    {
        var replacesCount = Replaces?.Length ?? 0;

        switch ( Kind )
        {
            case MigrationRecordKind.Squash when replacesCount == 0:
                throw new MigrationLedgerIntegrityException(
                    $"Ledger row '{Id}' has Kind=Squash but an empty Replaces set.", Id );
            case MigrationRecordKind.Migration when replacesCount > 0:
                throw new MigrationLedgerIntegrityException(
                    $"Ledger row '{Id}' has Kind=Migration but a non-empty Replaces set ({replacesCount} entries).", Id );
            case MigrationRecordKind.Recovery when replacesCount == 0:
                throw new MigrationLedgerIntegrityException(
                    $"Ledger row '{Id}' has Kind=Recovery but an empty Replaces set " +
                    "(the missing-versions list is required for token re-verification).", Id );
            case MigrationRecordKind.Recovery when string.IsNullOrWhiteSpace( Checksum ):
                throw new MigrationLedgerIntegrityException(
                    $"Ledger row '{Id}' has Kind=Recovery but no Checksum " +
                    "(the deterministic acknowledgement token is required).", Id );
        }
    }
}
