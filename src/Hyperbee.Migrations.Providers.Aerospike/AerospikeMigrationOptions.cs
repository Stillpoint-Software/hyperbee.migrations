namespace Hyperbee.Migrations.Providers.Aerospike;

public class AerospikeMigrationOptions : MigrationOptions
{
    public const string DefaultNamespace = "test";
    public const string DefaultMigrationSet = "SchemaMigrations";
    public const string DefaultLockName = "migration_lock";

    public string Namespace { get; set; }
    public string MigrationSet { get; set; }
    public string LockName { get; set; }

    // Lock TTL applied to the Aerospike lock record at acquisition. The record
    // expires server-side after this interval, so a crashed runner's lock is
    // automatically reclaimed. Kept short — auto-renew (LockRenewInterval) keeps
    // a healthy runner's lock alive.
    public TimeSpan LockExpireInterval { get; set; }

    // How often the runner re-touches the lock record to extend its TTL while
    // the migration is still executing. Must be shorter than LockExpireInterval
    // to avoid expiry between renewals.
    public TimeSpan LockRenewInterval { get; set; }

    // Hard ceiling on total lock lifetime. Once a runner has held the lock for
    // this long, renewals stop and the lock is allowed to expire. Protects
    // against a buggy migration that loops forever.
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

        LockExpireInterval = TimeSpan.FromSeconds( 60 );
        LockRenewInterval = TimeSpan.FromSeconds( 30 );
        LockMaxLifetime = TimeSpan.FromHours( 1 );
    }

    public void Deconstruct( out string @namespace, out string migrationSet, out string lockName )
    {
        @namespace = Namespace;
        migrationSet = MigrationSet;
        lockName = LockName;
    }
}
