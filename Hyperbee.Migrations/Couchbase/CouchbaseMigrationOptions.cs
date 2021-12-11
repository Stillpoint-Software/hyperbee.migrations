using System;

namespace Hyperbee.Migrations.Couchbase;

public class CouchbaseMigrationOptions : MigrationOptions
{
    public string BucketName { get; set; }
    public string ScopeName { get; set; }
    public string CollectionName { get; set; }

    public string MutexName { get; set; }
    public TimeSpan MutexExpireInterval { get; set; }
    public TimeSpan MutexMaxLifetime { get; set; }
    public TimeSpan MutexRenewInterval { get; set; }

    public CouchbaseMigrationOptions() 
        : this( null )
    {
    }

    public CouchbaseMigrationOptions( IMigrationActivator migrationActivator ) 
        : base( migrationActivator )
    {
        ScopeName = "_default";
        CollectionName = "ledger";

        MutexMaxLifetime = TimeSpan.FromHours( 1 );
        MutexExpireInterval = TimeSpan.FromMinutes( 5 );
        MutexRenewInterval = TimeSpan.FromMinutes( 2 );
    }
}