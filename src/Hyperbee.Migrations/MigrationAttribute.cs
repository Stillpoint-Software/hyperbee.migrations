using System;
using System.Collections.Generic;
using System.Linq;

namespace Hyperbee.Migrations;

[AttributeUsage( AttributeTargets.Class, Inherited = false, AllowMultiple = false )]
public class MigrationAttribute : Attribute
{
    public long Version { get; set; }
    public IEnumerable<string> Profiles { get; set; }
    public string Description { get; set; } = string.Empty;

    public MigrationAttribute( long version )
        : this( version, Array.Empty<string>() )
    {
    }

    public MigrationAttribute( long version, params string[] profiles )
    {
        Version = version;
        Profiles = profiles ?? Enumerable.Empty<string>();
    }
}