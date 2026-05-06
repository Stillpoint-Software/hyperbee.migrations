namespace Hyperbee.Migrations;

/// <summary>
/// Computes a deterministic checksum for a migration prior to ledger write.
/// The checksum is stored on the <see cref="MigrationRecord"/> and used by
/// the squash codegen path to detect migrations whose definition changed
/// after the ledger row was written (per ADR-0021).
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be deterministic for a given migration definition —
/// running the strategy twice against the same migration must produce the
/// same digest. Non-determinism (timestamps, random salts, machine identity)
/// invalidates the C12 generation determinism gate (per ADR-0019).
/// </para>
/// <para>
/// The default implementation hashes <c>FullName ‖ Version</c> for code-only
/// migrations. Per-provider overrides may extend this to hash resource bytes
/// (sorted by resource name) for resource-based migrations — see ADR-0021
/// "Default checksum strategy".
/// </para>
/// </remarks>
public interface IChecksumStrategy
{
    /// <summary>
    /// Returns a deterministic digest (typically a hex-encoded SHA-256 string)
    /// for the supplied migration.
    /// </summary>
    Task<string> ComputeAsync(
        Migration migration,
        MigrationAttribute attribute,
        CancellationToken cancellationToken = default );
}
