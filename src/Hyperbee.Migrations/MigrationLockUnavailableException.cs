using System;
using System.Runtime.Serialization;

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

    internal MigrationLockUnavailableException( SerializationInfo info, StreamingContext context )
    : base( info, context )
    {
    }
}