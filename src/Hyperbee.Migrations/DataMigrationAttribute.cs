namespace Hyperbee.Migrations;

/// <summary>
/// Marks a migration class as carrying user data operations
/// (INSERT/UPDATE/DELETE/MERGE/COPY/etc.) per ADR-0019 amendment A5.
/// The squash codegen treats <see cref="DataMigrationAttribute"/>-marked
/// migrations as non-elidable: they're carried forward verbatim in the
/// squash body even when their structural changes are subsumed by a
/// canonical snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The complement is <see cref="StructuralOnlyAttribute"/>, which asserts
/// the migration is purely structural (DDL only). Together the two
/// attributes are the load-bearing signal for the squash codegen: a
/// migration without either annotation that fails the data-op classifier's
/// structural test is refused at squash time so the operator must declare
/// intent.
/// </para>
/// <para>
/// Apply at the class level. Attribute is inherited so a base
/// <see cref="Migration"/> subclass can declare it once for a family of
/// derived migrations.
/// </para>
/// </remarks>
[AttributeUsage( AttributeTargets.Class, Inherited = true, AllowMultiple = false )]
public sealed class DataMigrationAttribute : Attribute
{
    /// <summary>
    /// Optional human-readable description of the data operation. Used by
    /// the squash codegen's <c>summary.md</c> artifact for review context.
    /// </summary>
    public string Reason { get; init; } = "";
}
