namespace Hyperbee.Migrations;

[AttributeUsage( AttributeTargets.Class, Inherited = false, AllowMultiple = false )]
public class MigrationAttribute : Attribute
{
    public long Version { get; set; }
    public IEnumerable<string> Profiles { get; set; }
    public string StartMethod { get; set; }
    public string StopMethod { get; set; }
    public bool Journal { get; set; }

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
