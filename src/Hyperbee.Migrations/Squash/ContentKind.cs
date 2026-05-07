namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Classifies the form of generated squash content. Drives downstream
/// canonicalization, file extension choice, and dispatcher selection
/// (per ADR-0019 / ADR-0022).
/// </summary>
public enum ContentKind : byte
{
    /// <summary>SQL text (Postgres v1; potentially other RDBMS-shaped providers).</summary>
    SqlText = 0,

    /// <summary>C# source emitting a programmatic migration body.</summary>
    CSharpSource = 1,

    /// <summary>Canonicalized JSON (e.g., MongoDB Extended JSON / OpenSearch index template).</summary>
    CanonicalJson = 2,

    /// <summary>Opaque binary payload — provider-specific custom format.</summary>
    OpaqueBinary = 3
}
