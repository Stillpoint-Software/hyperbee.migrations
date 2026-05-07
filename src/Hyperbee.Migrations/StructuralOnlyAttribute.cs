namespace Hyperbee.Migrations;

/// <summary>
/// Marks a migration class as purely structural (DDL only — no
/// INSERT/UPDATE/DELETE/MERGE/COPY) per ADR-0019 amendment A5. The squash
/// codegen treats <see cref="StructuralOnlyAttribute"/>-marked migrations
/// as elidable: their structural effects are subsumed by the canonical
/// snapshot and they're not carried forward verbatim.
/// </summary>
/// <remarks>
/// The complement is <see cref="DataMigrationAttribute"/>. Together the two
/// attributes are the load-bearing signal for the squash codegen.
/// </remarks>
[AttributeUsage( AttributeTargets.Class, Inherited = true, AllowMultiple = false )]
public sealed class StructuralOnlyAttribute : Attribute
{
}
