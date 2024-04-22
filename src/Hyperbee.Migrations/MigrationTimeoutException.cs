using System;

namespace Hyperbee.Migrations;

[Serializable]
public class MigrationTimeoutException : MigrationException
{

    public MigrationTimeoutException()
        : base( "Migration timeout exception" )
    {
    }

    public MigrationTimeoutException( string message )
        : base( message )
    {
    }

    public MigrationTimeoutException( string message, Exception innerException )
        : base( message, innerException )
    {
    }
}
