using System;
using System.Linq;
using System.Text;

namespace Hyperbee.Migrations.Providers.Couchbase.Parsers;

public record KeyspaceRef
{
    public string Namespace { get; }
    public string BucketName { get; }
    public string ScopeName { get; }
    public string CollectionName { get; }

    public KeyspaceRef( string ns, string bucketName, string scopeName, string collectionName )
    {
        Namespace = Unquote( ns );
        BucketName = Unquote( bucketName );
        ScopeName = Unquote( scopeName );
        CollectionName = Unquote( collectionName );
    }

    private static string Unquote( ReadOnlySpan<char> value )
    {
        var result = value.Trim().Trim( "`'\"" );
        return result.IsEmpty ? null : result.ToString();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();

        if ( !string.IsNullOrWhiteSpace( Namespace ) )
            builder.Append( $"`{Namespace}`:" );

        builder.AppendJoin( '.', new[] { BucketName, ScopeName, CollectionName }
            .Where( x => !string.IsNullOrEmpty( x ) )
            .Select( x => $"`{x}`" )
        );

        return builder.ToString();
    }

    public void Deconstruct( out string ns, out string bucketName, out string scopeName, out string collectionName )
    {
        ns = Namespace;
        bucketName = BucketName;
        scopeName = ScopeName;
        collectionName = CollectionName;
    }
}