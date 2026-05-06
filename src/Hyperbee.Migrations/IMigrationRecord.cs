namespace Hyperbee.Migrations;

public interface IMigrationRecord
{
    string Id { get; }
    DateTimeOffset RunOn { get; }

    /// <summary>
    /// SHA-256 (or other deterministic) digest of the migration's resource bytes
    /// or definition, computed at write time per <see cref="IChecksumStrategy{TMigration}"/>.
    /// Null on rows written by Hyperbee.Migrations v2.x (pre-checksum era).
    /// </summary>
    string Checksum { get; }

    /// <summary>
    /// Classifies the row by migration kind. Pre-checksum-era rows read as
    /// <see cref="MigrationRecordKind.Migration"/> by default.
    /// </summary>
    MigrationRecordKind Kind { get; }

    /// <summary>
    /// For <see cref="MigrationRecordKind.Squash"/> rows, the set of prior
    /// migration versions this squash subsumes. Empty for
    /// <see cref="MigrationRecordKind.Migration"/> rows. Per ADR-0021 (A1)
    /// the Kind/Replaces relationship is a ledger integrity invariant.
    /// </summary>
    IReadOnlyList<long> Replaces { get; }
}
