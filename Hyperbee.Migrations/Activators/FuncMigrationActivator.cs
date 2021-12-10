using System;

namespace Hyperbee.Migrations.Activators;

public class FuncMigrationActivator : IMigrationActivator
{
    public static readonly Func<Type, Migration> DefaultFactory = type => (Migration) Activator.CreateInstance( type );
    public Func<Type, Migration> ActivationFactory { get; set; } = DefaultFactory;

    public Migration CreateInstance( Type migrationType )
    {
        return ActivationFactory( migrationType );
    }
}