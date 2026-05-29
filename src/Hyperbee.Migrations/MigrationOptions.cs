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
    /// When true, the runner bypasses the interruption lockout (per ADR-0027):
    /// a leftover in-flight sentinel from a previously interrupted
    /// <c>[DataMigration]</c> (or unannotated) migration is reaped and the
    /// migration re-runs, instead of throwing <see cref="MigrationInterruptedException"/>.
    /// The operator should set this only after verifying the live data state is
    /// consistent with re-running the interrupted migration. Default false.
    /// Provider option subclasses that previously exposed their own
    /// <c>ForceResume</c> (e.g. the OpenSearch down-path lockout) delegate to
    /// this base flag.
    /// </summary>
    public bool ForceResume { get; set; }

    /// <summary>
    /// Strategy used to compute the <see cref="MigrationRecord.Checksum"/>
    /// stamped on each ledger row at write time (per ADR-0021).
    /// Defaults to <see cref="DefaultChecksumStrategy"/>; provider integrations
    /// may swap in resource-bytes-aware implementations.
    /// </summary>
    public IChecksumStrategy ChecksumStrategy { get; set; }

    /// <summary>
    /// Logical name of the environment this runner instance acts against
    /// ("prod-eu-1", "staging", "dev-laptop-42", etc.). Used to derive the
    /// deterministic mid-range squash recovery acknowledgement token and the
    /// fleet-readiness identity. Optional; when null, the recovery token is
    /// computed against a synthetic "&lt;unset&gt;" environment name and the
    /// exception message includes a remediation note recommending an explicit
    /// value. Per ADR-0019 A3 + R-10/RB-2 audit follow-ups.
    /// </summary>
    public string EnvironmentName { get; set; }
}
