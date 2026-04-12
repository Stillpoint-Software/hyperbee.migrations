namespace Hyperbee.Migrations.Providers.Aerospike;

public class AerospikeMigrationOptions : MigrationOptions
{
    public const string DefaultNamespace = "test";
    public const string DefaultMigrationSet = "SchemaMigrations";
    public const string DefaultLockName = "migration_lock";

    public string Namespace { get; set; }
    public string MigrationSet { get; set; }
    public string LockName { get; set; }
    public TimeSpan LockMaxLifetime { get; set; }

    public AerospikeMigrationOptions()
        : this( null )
    {
    }

    public AerospikeMigrationOptions( IMigrationActivator migrationActivator )
        : base( migrationActivator )
    {
        Namespace = DefaultNamespace;
        MigrationSet = DefaultMigrationSet;
        LockName = DefaultLockName;

        LockMaxLifetime = TimeSpan.FromHours( 1 );
    }

    public void Deconstruct( out string @namespace, out string migrationSet, out string lockName )
    {
        @namespace = Namespace;
        migrationSet = MigrationSet;
        lockName = LockName;
    }
}
