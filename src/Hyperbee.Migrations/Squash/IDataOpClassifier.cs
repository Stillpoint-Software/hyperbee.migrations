namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Classifies a statement or call site for squash codegen purposes.
/// Per-provider implementations scan provider-native statement shapes
/// (Postgres SQL, OpenSearch verbs, etc.) plus C# call sites in code-only
/// migrations.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0019 (A5, A8): the classifier is the load-bearing signal for
/// "this migration carries user data and must be preserved verbatim in the
/// squash". The default-deny posture means an unclassified call site
/// raises <see cref="DataOpClassification.IsUnclassified"/> and forces
/// the squash to refuse until the operator annotates the migration with
/// <c>[DataMigration]</c> or <c>[StructuralOnly]</c>.
/// </para>
/// <para>
/// Provider implementations may consult a non-determinism scan helper
/// (per ADR-0019 A8) to flag <c>DateTime.Now</c>, <c>Guid.NewGuid</c>,
/// <c>Random</c> sans seed, machine identity, etc. — non-determinism
/// breaks the C12 generation determinism gate and must be surfaced
/// before squash codegen runs.
/// </para>
/// </remarks>
public interface IDataOpClassifier
{
    /// <summary>
    /// Classifies a statement or call site. The string form passed in is
    /// the verbatim text of the statement (or stringified call-site location
    /// for code-only migrations); the implementation chooses how to parse it.
    /// </summary>
    DataOpClassification Classify( string statementOrCallSite );
}
