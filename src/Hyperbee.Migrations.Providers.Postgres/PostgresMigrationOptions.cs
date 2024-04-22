namespace Hyperbee.Migrations.Providers.Postgres;

public class PostgresMigrationOptions : MigrationOptions
{
    public const string DefaultSchema = "migration";
    public const string DefaultMigrationTable = "ledger";
    public const string DefaultLockTable = "ledger_lock";

    public string SchemaName { get; set; }
    public string TableName { get; set; }
    public string LockName { get; set; }

    public PostgresMigrationOptions()
    : this( null )
    {
    }

    public PostgresMigrationOptions( IMigrationActivator migrationActivator )
    : base( migrationActivator )
    {
        SchemaName = DefaultSchema;
        TableName = DefaultMigrationTable;
        LockName = DefaultLockTable;
    }

    public void Deconstruct( out string schemaName, out string tableName, out string lockName )
    {
        schemaName = SchemaName;
        tableName = TableName;
        lockName = LockName;
    }
}
