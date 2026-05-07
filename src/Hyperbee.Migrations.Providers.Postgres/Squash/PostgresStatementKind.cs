namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Classification of a single Postgres SQL statement for squash codegen.
/// Drives canonical-formatter selection and per-(kind, name) set diffing
/// in the generator (per ADR-0019).
/// </summary>
public enum PostgresStatementKind : byte
{
    Unknown,

    // pg_dump session preamble
    SetParameter,
    SelectPgCatalog,

    // schema-shape DDL
    CreateExtension,
    CreateSchema,
    CreateType,
    CreateDomain,
    CreateSequence,
    AlterSequence,
    CreateTable,
    CreateTablePartitionOf,
    AlterTable,
    AlterTableEnableRls,
    AlterTableAttachPartition,
    AlterTableAddConstraint,
    CreateIndex,
    CreateUniqueIndex,
    AlterIndex,
    AlterIndexAttachPartition,
    CreateView,
    CreateMaterializedView,
    CreateFunction,
    CreateProcedure,
    CreateTrigger,
    CreatePolicy,
    AlterPolicy,
    CreateRole,
    GrantRevoke,
    Comment,

    // DROP family — squash codegen emits these when objects disappear
    // between snapshot A and snapshot B
    DropTable,
    DropIndex,
    DropView,
    DropMaterializedView,
    DropFunction,
    DropProcedure,
    DropTrigger,
    DropType,
    DropDomain,
    DropSequence,
    DropPolicy,
    DropSchema,
    DropExtension
}
