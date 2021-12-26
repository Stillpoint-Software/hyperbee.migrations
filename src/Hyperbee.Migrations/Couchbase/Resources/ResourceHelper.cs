using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Hyperbee.Migrations.Couchbase.Resources;

public static class ResourceHelper
{
    public static string GetResource<TType>( string name, bool fullyQualified = false )
    {
        var fullyQualifiedName = fullyQualified ? name : GetResourceName<TType>( name );

        using var stream = typeof(TType).Assembly.GetManifestResourceStream( fullyQualifiedName );

        if ( stream == null )
            throw new FileNotFoundException( $"Cannot find '{fullyQualifiedName}'." );

        using var reader = new StreamReader( stream );
        return reader.ReadToEnd();
    }

    public static string GetResourceName<TType>( string name )
    {
        if ( name == null )
            throw new ArgumentNullException( nameof(name) );

        var ns = typeof(TType) // look for assembly attribute
            .Assembly
            .GetCustomAttributes( typeof(ResourceLocationAttribute), false )
            .Cast<ResourceLocationAttribute>()
            .Select( x => x.RootNamespace )
            .FirstOrDefault() ?? typeof(TType).Namespace; // default to type namespace

        var key = SanitizeName( name );

        return $"{ns}.{key}";
    }

    public static string[] GetResourceNames<TType>()
    {
        return typeof(TType).Assembly.GetManifestResourceNames();
    }

    private static readonly char[] InvalidChars = {
        ' ',
        '\u00A0' /* non-breaking space */,  ',', ';', '|', '~', '@',
        '#', '%', '^', '&', '*', '+', '-', '/', '\\', '<', '>', '?', '[',
        ']', '(', ')', '{', '}', '\"', '\'', '!', '`', '='
    };

    private static string SanitizeName( ReadOnlySpan<char> name )
    {
        name = name.Trim( '/' ); // allow path like names

        if ( name.IsEmpty )
            return "_";

        var builder = new StringBuilder( name.Length + 1 );

        if ( char.IsDigit( name[0] ) )
            builder.Append( '_' );

        foreach ( var c in name )
        {
            if ( c == '/' ) // path
                builder.Append( '.' );
            else
                builder.Append( InvalidChars.Contains( c ) ? '_' : c );
        }

        return builder.ToString();
    }
}