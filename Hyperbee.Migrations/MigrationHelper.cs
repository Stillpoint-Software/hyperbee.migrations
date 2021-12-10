using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hyperbee.Migrations;

public static class MigrationHelper
{
    public static readonly string DefaultMigrationIdPrefix = "MigrationRecord";

    public static string GetMigrationDocumentId( Migration migration, char separator )
    {
        static string SafeName( Type type, char separator )
        {
            // convert underscores to separator and eliminate repetition
            const char Underscore = '_';
            var idSafeTypeName = Regex.Replace( type.Name, Underscore + "{2,}", Underscore.ToString() ).Trim( Underscore );
            return idSafeTypeName.Replace( Underscore, separator ).ToLowerInvariant();
        }

        var type = migration.GetType();
        var name = SafeName( type, separator );
        var version = type.GetMigrationAttribute().Version;

        return string.Join( separator.ToString(), DefaultMigrationIdPrefix, name, version.ToString() ).ToLowerInvariant();
    }

    public static MigrationAttribute GetMigrationAttribute( this Type type )
    {
        var attribute = Attribute
            .GetCustomAttributes( type )
            .FirstOrDefault( x => x is MigrationAttribute );

        return (MigrationAttribute) attribute;
    }

    public static readonly Func<Type, bool> IsMigration = type => typeof(Migration).IsAssignableFrom( type ) && !type.IsAbstract;
}