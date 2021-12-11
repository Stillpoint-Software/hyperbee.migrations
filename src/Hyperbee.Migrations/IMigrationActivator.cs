using System;

namespace Hyperbee.Migrations
{
    public interface IMigrationActivator
    {
        Migration CreateInstance( Type migrationType );
    }
}