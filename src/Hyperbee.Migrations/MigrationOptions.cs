using System.Collections.Generic;
using System.Reflection;

namespace Hyperbee.Migrations;

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
        ToVersion = 0;
        // Default-on for production safety (R-1 per ADR-0024 audit follow-up). Operators
        // who deliberately want lockless dev/test runs must opt out explicitly.
        LockingEnabled = true;
        MigrationActivator = migrationActivator;
        Conventions = new DefaultMigrationConventions();
        ChecksumStrategy = new DefaultChecksumStrategy();
    }

    public Direction Direction { get; set; }
    public IList<Assembly> Assemblies { get; set; }
    public IList<string> Profiles { get; set; }
    public long ToVersion { get; set; }

    public bool LockingEnabled { get; set; }
    public IMigrationActivator MigrationActivator { get; set; }
    public IMigrationConventions Conventions { get; set; }

    /// <summary>
    /// Strategy used to compute the <see cref="MigrationRecord.Checksum"/>
    /// stamped on each ledger row at write time (per ADR-0021).
    /// Defaults to <see cref="DefaultChecksumStrategy"/>; provider integrations
    /// may swap in resource-bytes-aware implementations.
    /// </summary>
    public IChecksumStrategy ChecksumStrategy { get; set; }
}
