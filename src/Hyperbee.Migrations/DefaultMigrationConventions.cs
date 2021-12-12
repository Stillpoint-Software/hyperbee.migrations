using System.Reflection;
using System.Text.RegularExpressions;

namespace Hyperbee.Migrations;

public class DefaultMigrationConventions : IMigrationConventions
{
    public static readonly string DefaultRecordIdPrefix = "MigrationRecord";
    private const string Separator = ".";

    public string GetRecordId( Migration migration )
    {
        var type = migration.GetType();

        if ( type.GetCustomAttribute( typeof(MigrationAttribute) ) is not MigrationAttribute attribute )
            throw new MigrationException( $"Migration `{type.Name}` is missing `{nameof(MigrationAttribute)}`." );

        // convert underscores to separator char, eliminate repetition, trim from front and back.
        // '__ONE_Two___Three_' => 'one.two.three'

        var name = Regex.Replace( type.Name, "_{1,}", Separator ).Trim( Separator[0] );
        var version = attribute.Version.ToString();

        return string.Join( Separator, DefaultRecordIdPrefix, name, version ).ToLowerInvariant();
    }
}