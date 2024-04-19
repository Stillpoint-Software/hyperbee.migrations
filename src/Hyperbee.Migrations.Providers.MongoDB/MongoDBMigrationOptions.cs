namespace Hyperbee.Migrations.Providers.MongoDB;

public class MongoDBMigrationOptions : MigrationOptions
{
    public const string DefaultDatabase = "migration";
    public const string DefaultCollection = "ledger";

    public string DatabaseName { get; set; }
    public string CollectionName { get; set; }

    public MongoDBMigrationOptions() 
    : this( null )
    {
    }

    public MongoDBMigrationOptions( IMigrationActivator migrationActivator ) 
    : base( migrationActivator )
    {
        DatabaseName = DefaultDatabase;
        CollectionName = DefaultCollection;
    }

    public void Deconstruct( out string databaseName, out string collectionName )
    {
        databaseName = DatabaseName;
        collectionName = CollectionName;
    }
}