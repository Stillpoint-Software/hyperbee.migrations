using System.Reflection;
using System.Text.RegularExpressions;

namespace Hyperbee.Migrations;

public class DefaultMigrationConventions : IMigrationConventions
{
    public static readonly string DefaultRecordIdPrefix = "Record";
    private const char ReplaceChar = '-';

    public string GetRecordId( Migration migration )
    {
        var type = migration.GetType();

        if ( type.GetCustomAttribute( typeof( MigrationAttribute ) ) is not MigrationAttribute attribute )
            throw new MigrationException( $"Migration `{type.Name}` is missing `{nameof( MigrationAttribute )}`." );

        // convert underscores to the replacement char, eliminate repetition, and trim front and back.
        // '__ONE_Two___Three_' => 'one-two-three'

        var name = Regex.Replace( type.Name, "_{1,}", ReplaceChar.ToString() ).Trim( ReplaceChar );
        var version = attribute.Version.ToString();

        return string.Join( '.', DefaultRecordIdPrefix, version, name ).ToLowerInvariant();
    }
}
