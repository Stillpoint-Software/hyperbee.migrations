using System.Reflection;

namespace Hyperbee.Migrations;

public abstract class Migration
{
    public static string VersionedName<TMigration>()
        where TMigration : Migration
    {
        var type = typeof( TMigration );

        if ( type.GetCustomAttribute( typeof( MigrationAttribute ) ) is not MigrationAttribute attribute )
            throw new MigrationException( $"Migration `{type.Name}` is missing `{nameof( MigrationAttribute )}`." );

        return $"{attribute.Version}-{type.Name}";
    }

    public abstract Task UpAsync( CancellationToken cancellationToken = default );
    public virtual Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}
