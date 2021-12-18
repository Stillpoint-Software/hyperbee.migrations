﻿using System.Text;

// ReSharper disable once CheckNamespace
namespace Hyperbee.Migrations.Samples.Resources;

public static class ResourceHelper
{
    public static string GetResource( string locator, string name )
    {
        var resourceName = GetResourceName( locator, name );
        return GetResource( resourceName );
    }

    public static string GetResource( string fullyQualifiedName )
    {
        using var stream = typeof(ResourceHelper).Assembly.GetManifestResourceStream( fullyQualifiedName );

        if ( stream == null )
            throw new FileNotFoundException( $"Cannot find '{fullyQualifiedName}'." );

        using var reader = new StreamReader( stream );
        return reader.ReadToEnd();
    }

    public static string GetResourceName( string locator, string name )
    {
        if ( locator == null )
            throw new ArgumentNullException( nameof(locator) );

        if ( name == null )
            throw new ArgumentNullException( nameof(name) );

        var key = GetResourceName( $"{locator}.{name}" );

        return $"{typeof(ResourceHelper).Namespace}.{key}";
    }

    public static string[] GetManifestResourceNames()
    {
        return typeof(ResourceHelper).Assembly.GetManifestResourceNames();
    }

    private static readonly char[] InvalidChars = {
        ' ',
        '\u00A0' /* non-breaking space */,  ',', ';', '|', '~', '@',
        '#', '%', '^', '&', '*', '+', '-', '/', '\\', '<', '>', '?', '[',
        ']', '(', ')', '{', '}', '\"', '\'', '!'
    };

    private static string GetResourceName( string key )
    {
        if ( string.IsNullOrEmpty( key ) )
            return "_";

        var builder = new StringBuilder( key.Length + 1 );

        if ( char.IsDigit( key[0] ) )
            builder.Append( '_' );

        foreach ( var c in key )
        {
            if ( c == '/' )
                builder.Append( '.' );
            else
                builder.Append( InvalidChars.Contains( c ) ? '_' : c );
        }

        return builder.ToString();
    }
}