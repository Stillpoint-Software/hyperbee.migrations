namespace Hyperbee.Migrations.Providers.MongoDB;

public class MongoDBMigrationOptions : MigrationOptions
{
    public const string DefaultDatabase = "migration";
    public const string DefaultCollection = "ledger";
    public const string DefaultLockCollection = "ledger";

    public string DatabaseName { get; set; }
    public string CollectionName { get; set; }
    public string LockName { get; set; }
    public TimeSpan LockMaxLifetime { get; set; }

    public MongoDBMigrationOptions()
        : this( null )
    {
    }

    public MongoDBMigrationOptions( IMigrationActivator migrationActivator )
        : base( migrationActivator )
    {
        DatabaseName = DefaultDatabase;
        CollectionName = DefaultCollection;
        LockName = DefaultLockCollection;

        LockMaxLifetime = TimeSpan.FromHours( 1 );
    }

    public void Deconstruct( out string databaseName, out string collectionName, out string lockName )
    {
        databaseName = DatabaseName;
        collectionName = CollectionName;
        lockName = LockName;
    }
}
