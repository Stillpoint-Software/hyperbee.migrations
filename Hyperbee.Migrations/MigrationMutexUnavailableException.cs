using System;
using System.Runtime.Serialization;

namespace Hyperbee.Migrations;

[Serializable]
public class MigrationMutexUnavailableException : MigrationException
{
    public MigrationMutexUnavailableException()
        : base( "Migration mutex unavailable exception" )
    {
    }

    public MigrationMutexUnavailableException( string message )
        : base( message )
    {
    }

    public MigrationMutexUnavailableException( string message, Exception innerException )
        : base( message, innerException )
    {
    }

    internal MigrationMutexUnavailableException( SerializationInfo info, StreamingContext context )
        : base( info, context )
    {
    }
}