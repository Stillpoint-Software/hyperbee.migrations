using System;

namespace Hyperbee.Migrations;

[Serializable]
public class MigrationLockUnavailableException : MigrationException
{
    public MigrationLockUnavailableException()
    : base( "Migration lock unavailable exception" )
    {
    }

    public MigrationLockUnavailableException( string message )
    : base( message )
    {
    }

    public MigrationLockUnavailableException( string message, Exception innerException )
    : base( message, innerException )
    {
    }
}