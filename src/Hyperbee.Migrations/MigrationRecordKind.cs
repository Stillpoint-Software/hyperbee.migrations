namespace Hyperbee.Migrations;

/// <summary>
/// Classifies a ledger row by the kind of migration that wrote it.
/// </summary>
/// <remarks>
/// <para>
/// Pre-checksum-era rows (written by Hyperbee.Migrations v2.x) read as
/// <see cref="Migration"/> by default. The numeric values are stable and
/// part of the on-disk ledger contract: do not renumber.
/// </para>
/// </remarks>
public enum MigrationRecordKind : byte
{
    /// <summary>
    /// A regular migration. The row's <c>Replaces</c> set must be empty.
    /// </summary>
    Migration = 0,

    /// <summary>
    /// A squash migration that replaces a contiguous range of prior versions.
    /// The row's <c>Replaces</c> set must be non-empty.
    /// </summary>
    Squash = 1,

    /// <summary>
    /// A baseline marker installed when adopting Hyperbee.Migrations against
    /// an existing database. Reserved for future use.
    /// </summary>
    Baseline = 2
}
