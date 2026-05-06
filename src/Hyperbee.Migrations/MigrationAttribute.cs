namespace Hyperbee.Migrations;

[AttributeUsage( AttributeTargets.Class, Inherited = false, AllowMultiple = false )]
public class MigrationAttribute : Attribute
{
    public long Version { get; set; }
    public IEnumerable<string> Profiles { get; set; }
    public string StartMethod { get; set; }
    public string StopMethod { get; set; }
    public bool Journal { get; set; }
    public string Cron { get; set; }

    /// <summary>
    /// Explicit set of prior migration versions this migration replaces (squash).
    /// Empty for regular migrations. When non-empty, the migration is recognized
    /// as a squash and is recorded with <see cref="MigrationRecordKind.Squash"/>
    /// per ADR-0019. Combine with <see cref="ReplacesRange"/> to express
    /// contiguous ranges concisely; the union of both forms is resolved against
    /// the assembly's actual <see cref="MigrationAttribute"/>-decorated types
    /// at discovery time.
    /// </summary>
    public long[] Replaces { get; set; } = Array.Empty<long>();

    /// <summary>
    /// Range syntax for the replaced version set, e.g. <c>"1000-1500, 1700, 1800-1850"</c>.
    /// Each comma-separated term is either a single version or an inclusive
    /// <c>start-end</c> range. Resolved against the assembly's actual
    /// <see cref="MigrationAttribute"/>-decorated types at discovery time;
    /// any version named here that doesn't correspond to a discovered migration
    /// raises <see cref="MigrationLoadException"/>.
    /// </summary>
    public string ReplacesRange { get; set; }

    public MigrationAttribute( long version, params string[] profiles ) : this( version, null, null, true, profiles ) { }



    public MigrationAttribute( long version, string startMethod = null, string stopMethod = null, bool journal = true )
        : this( version, startMethod, stopMethod, journal, Array.Empty<string>() )
    {
    }

    public MigrationAttribute( long version, string startMethod = null, string stopMethod = null, bool journal = true, params string[] profiles )
    {
        Version = version;
        Profiles = profiles ?? Enumerable.Empty<string>();
        StartMethod = startMethod;
        StopMethod = stopMethod;
        Journal = journal;
    }

}
