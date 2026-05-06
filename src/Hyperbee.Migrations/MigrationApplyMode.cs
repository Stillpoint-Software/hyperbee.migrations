namespace Hyperbee.Migrations;

/// <summary>
/// Classifies how a migration is being applied for the purposes of squash
/// reconciliation (per ADR-0019).
/// </summary>
public enum MigrationApplyMode : byte
{
    /// <summary>
    /// Fresh install — the ledger was empty when the runner started. A squash
    /// migration's <c>UpAsync</c> may safely create the squashed schema as a
    /// baseline because no prior versions are recorded.
    /// </summary>
    Fresh = 0,

    /// <summary>
    /// Partial catch-up — the ledger had at least one prior migration recorded
    /// when the runner started. Squash migrations whose <c>Replaces</c> set is
    /// fully satisfied by the ledger should auto-mark without running
    /// <c>UpAsync</c>; partially satisfied ranges raise
    /// <c>MidRangeSquashException</c> per ADR-0019.
    /// </summary>
    PartialCatchUp = 1
}
