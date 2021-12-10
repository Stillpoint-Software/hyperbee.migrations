using System;

namespace Hyperbee.Migrations.Activators;

public interface IMigrationActivator
{
    Migration CreateInstance( Type migrationType );
}