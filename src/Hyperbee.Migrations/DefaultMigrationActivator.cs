using System;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations;

public class DefaultMigrationActivator : IMigrationActivator
{
    private readonly IServiceProvider _serviceProvider;

    public DefaultMigrationActivator( IServiceProvider serviceProvider )
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException( nameof( serviceProvider ) );
    }

    public Migration CreateInstance( Type migrationType )
    {
        return (Migration) ActivatorUtilities.CreateInstance( _serviceProvider, migrationType );
    }
}
