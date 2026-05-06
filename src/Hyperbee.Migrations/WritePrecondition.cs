namespace Hyperbee.Migrations;

/// <summary>
/// Conditions a record-store write must satisfy. Used by the squash codegen
/// reconciliation path to make ledger writes idempotent under concurrent runners.
/// </summary>
public enum WritePrecondition : byte
{
    /// <summary>
    /// No precondition. Record is upserted.
    /// </summary>
    None = 0,

    /// <summary>
    /// The write succeeds only if no row with the same id exists. Concurrent
    /// runners reconciling the same squash converge benignly: the loser
    /// receives <see cref="WriteOutcome.AlreadyExistsBenign"/> after a
    /// checksum-equality re-check.
    /// </summary>
    MustNotExist = 1
}
