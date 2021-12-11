using System;
using System.Collections.Generic;
using System.Reflection;

namespace Hyperbee.Migrations
{
    public class MigrationOptions
    {
        public MigrationOptions()
            : this( null )
        {
        }

        public MigrationOptions( IMigrationActivator migrationActivator )
        {
            Direction = Direction.Up;
            Assemblies = new List<Assembly>();
            Profiles = new List<string>();
            Assemblies = new List<Assembly>();
            ToVersion = 0;
            LockingEnabled = false;
            MigrationActivator = migrationActivator;
            Conventions = new DefaultMigrationConventions();
        }

        public Direction Direction { get; set; }
        public IList<Assembly> Assemblies { get; set; }
        public IList<string> Profiles { get; set; }
        public long ToVersion { get; set; }

        public bool LockingEnabled { get; set; }
        public IMigrationActivator MigrationActivator { get; set; }
        public IMigrationConventions Conventions { get; set; }
    }
}