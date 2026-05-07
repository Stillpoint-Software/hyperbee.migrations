namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Captures the topology axes that affect squash generation determinism for a
/// provider — server major/minor version, installed extensions, locale provider,
/// encoding, etc. Two signatures represent compatible topologies if and only if
/// <see cref="IsCompatibleWith"/> returns true.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0019 (A14): when a provider adds a new topology axis (e.g., a new
/// extension type, a new collation provider), bump <see cref="SchemaVersion"/>
/// and add a back-compat shim in <see cref="IsCompatibleWith"/>. Signature
/// shape changes require a new ADR documenting the back-compat rules.
/// </para>
/// <para>
/// The framework hashes signatures into the verification round's container
/// fingerprint and into the snapshot-A cache key (per consensus C13). Two
/// runners with incompatible signatures do not share cache entries.
/// </para>
/// </remarks>
public interface ITopologySignature
{
    /// <summary>
    /// Schema version of the signature shape. Bumped when a provider adds a
    /// new axis or changes the compatibility rules.
    /// </summary>
    int SchemaVersion { get; }

    /// <summary>
    /// Provider identifier ("postgres", "mongodb", etc.). Used to key the
    /// verification cache and to refuse cross-provider compares early.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// All signature axes as ordered key-value pairs. Per ADR-0019 the values
    /// must be deterministic (no timestamps, no machine identity).
    /// </summary>
    IReadOnlyDictionary<string, string> Properties { get; }

    /// <summary>
    /// Compares this signature to another. Returns true when the two
    /// topologies are compatible for squash purposes; otherwise sets
    /// <paramref name="reason"/> to a human-readable diagnostic.
    /// </summary>
    bool IsCompatibleWith( ITopologySignature other, out string reason );
}
