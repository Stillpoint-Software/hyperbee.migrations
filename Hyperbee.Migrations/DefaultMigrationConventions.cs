using System;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Hyperbee.Migrations;

public class DefaultMigrationConventions : IMigrationConventions
{
    public static readonly string DefaultMigrationIdPrefix = "MigrationRecord";

    public string GetRecordId( Migration migration, char separator )
    {
        static string SafeName( Type type, char separator )
        {
            // convert underscores to separator and eliminate repetition
            const char Underscore = '_';
            var idSafeTypeName = Regex.Replace( type.Name, Underscore + "{2,}", Underscore.ToString() ).Trim( Underscore );
            return idSafeTypeName.Replace( Underscore, separator ).ToLowerInvariant();
        }

        var type = migration.GetType();

        if ( type.GetCustomAttribute( typeof(MigrationAttribute) ) is not MigrationAttribute attribute )
            throw new MigrationException( $"Migration `{type.Name}` is missing `{nameof(MigrationAttribute)}`" );

        var name = SafeName( type, separator );
        var version = attribute.Version;

        return string.Join( separator.ToString(), DefaultMigrationIdPrefix, name, version.ToString() ).ToLowerInvariant();
    }
}