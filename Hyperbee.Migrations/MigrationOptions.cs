using System;
using System.Collections.Generic;
using System.Reflection;

namespace Hyperbee.Migrations;

public class MigrationOptions
{
    public MigrationOptions()
    {
    }

    public MigrationOptions( IMigrationActivator migrationActivator )
    {
        Direction = Direction.Up;
        Assemblies = new List<Assembly>();
        Profiles = new List<string>();
        Assemblies = new List<Assembly>();
        ToVersion = 0;

        MigrationActivator = migrationActivator; 
        Conventions = new DefaultMigrationConventions(); 

        ScopeName = "_default";
        CollectionName = "ledger";
        MutexEnabled = false;

        MutexMaxLifetime = TimeSpan.FromHours( 1 );
        MutexExpireInterval = TimeSpan.FromHours( 30 );
        MutexRenewInterval = TimeSpan.FromSeconds( 15 );
    }

    public Direction Direction { get; set; }
    public IList<Assembly> Assemblies { get; set; }
    public IList<string> Profiles { get; set; }
    public long ToVersion { get; set; }

    public string BucketName { get; set; }
    public string ScopeName { get; set; }
    public string CollectionName { get; set; }
    public bool MutexEnabled { get; set; }
    public string MutexName { get; set; }
    public TimeSpan MutexExpireInterval { get; set; }
    public TimeSpan MutexMaxLifetime { get; set; }
    public TimeSpan MutexRenewInterval { get; set; }
    public char IdSeparatorChar { get; set; } = '-';

    public IMigrationActivator MigrationActivator { get; set; }
    public IMigrationConventions Conventions { get; set; }
}