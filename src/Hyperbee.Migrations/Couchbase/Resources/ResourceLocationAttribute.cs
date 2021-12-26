using System;

namespace Hyperbee.Migrations.Couchbase.Resources;

[AttributeUsage(AttributeTargets.Assembly)]
public class ResourceLocationAttribute : Attribute
{
    public string RootNamespace { get; init; }

    public ResourceLocationAttribute( string rootNamespace )
    {
        RootNamespace = rootNamespace;
    }
}