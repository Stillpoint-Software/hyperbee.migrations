using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public abstract class Migration
{
    public string VersionedName()
    {
        var type = GetType();
        
        if ( type.GetCustomAttribute( typeof(MigrationAttribute) ) is not MigrationAttribute attribute )
            throw new MigrationException( $"Migration `{type.Name}` is missing `{nameof(MigrationAttribute)}`." );

        return $"{attribute.Version}-{type.Name}";
    }

    public abstract Task UpAsync( CancellationToken cancellationToken = default );
    public virtual Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}