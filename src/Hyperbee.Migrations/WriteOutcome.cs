namespace Hyperbee.Migrations;

/// <summary>
/// Result of a precondition-bearing record-store write.
/// </summary>
public enum WriteOutcome : byte
{
    /// <summary>
    /// The row did not previously exist and was written.
    /// </summary>
    Created = 0,

    /// <summary>
    /// The row already existed and the existing checksum matches the requested
    /// checksum (benign concurrent reconciliation). The caller should treat
    /// this as success.
    /// </summary>
    AlreadyExistsBenign = 1,

    /// <summary>
    /// The row already existed but the existing checksum does NOT match the
    /// requested checksum. The caller must surface a conflict diagnostic.
    /// </summary>
    PreconditionFailed = 2
}
