using System;

namespace Hyperbee.Migrations.Couchbase;

public class CouchbaseMigrationOptions : MigrationOptions
{
    public string BucketName { get; set; }
    public string ScopeName { get; set; }
    public string CollectionName { get; set; }

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
        ScopeName = "_default";
        CollectionName = "ledger";

        LockMaxLifetime = TimeSpan.FromHours( 1 );
        LockExpireInterval = TimeSpan.FromMinutes( 5 );
        LockRenewInterval = TimeSpan.FromMinutes( 2 );
    }
}