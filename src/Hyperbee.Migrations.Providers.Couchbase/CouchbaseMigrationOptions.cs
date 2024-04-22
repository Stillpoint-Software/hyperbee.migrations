using System;

namespace Hyperbee.Migrations.Providers.Couchbase;

public class CouchbaseMigrationOptions : MigrationOptions
{
    public string BucketName { get; set; }
    public string ScopeName { get; set; }
    public string CollectionName { get; set; }

    public TimeSpan ClusterReadyTimeout { get; set; }
    public TimeSpan ProvisionRetryInterval { get; set; }
    public int ProvisionAttempts { get; set; }

    public string LockName { get; set; }
    public TimeSpan LockExpireInterval { get; set; }
    public TimeSpan LockMaxLifetime { get; set; }
    public TimeSpan LockRenewInterval { get; set; }

    public CouchbaseMigrationOptions()
    : this( null )
    {
    }

    public CouchbaseMigrationOptions( IMigrationActivator migrationActivator )
    : base( migrationActivator )
    {
        ScopeName = "migrations";
        CollectionName = "ledger";

        ClusterReadyTimeout = TimeSpan.FromMinutes( 5 );
        ProvisionRetryInterval = TimeSpan.FromSeconds( 1 );
        ProvisionAttempts = 30;

        LockMaxLifetime = TimeSpan.FromHours( 1 );
        LockExpireInterval = TimeSpan.FromMinutes( 5 );
        LockRenewInterval = TimeSpan.FromMinutes( 2 );
    }

    public void Deconstruct( out string bucketName, out string scopeName, out string collectionName )
    {
        bucketName = BucketName;
        scopeName = ScopeName;
        collectionName = CollectionName;
    }
}
