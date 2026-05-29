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
    Baseline = 2,

    /// <summary>
    /// A pre-staged mid-range squash recovery acknowledgement (per ADR-0019 A3 +
    /// ADR-0024 audit follow-up RB-2). Written by
    /// <c>hyperbee-migrations recover from-mid-range</c>; consumed by the
    /// runner on next invocation to force-mark a mid-range squash without
    /// running its body. The row's <c>Replaces</c> set MUST be the
    /// missing-versions list and the <c>Checksum</c> field carries the
    /// deterministic acknowledgement token. The row is deleted by the runner
    /// once the squash is auto-marked, so a stale acknowledgement cannot
    /// force-mark a second time.
    /// </summary>
    Recovery = 3,

    /// <summary>
    /// A transient in-flight sentinel (per ADR-0027). Written at a derived
    /// sentinel id immediately before a migration's body runs and deleted once
    /// the migration's real journal row is committed. A sentinel surviving into
    /// the next run means that migration was interrupted mid-body (SIGTERM /
    /// SIGKILL / node death) and the journal row was never written. The row's
    /// <c>Replaces</c> set MUST be empty (symmetry with <see cref="Migration"/>).
    /// Sentinels live at their own ids and never collide with real migration
    /// rows, so the applied-set never observes them as "applied".
    /// </summary>
    InProgress = 4
}
