using System;

namespace Hyperbee.Migrations;

[Serializable]
public class DuplicateMigrationException : MigrationException
{
    public long Id { get; init; }

    public DuplicateMigrationException()
    : base( "Duplicate migration exception" )
    {
    }

    public DuplicateMigrationException( string message )
    : base( message )
    {
    }

    public DuplicateMigrationException( string message, Exception innerException )
    : base( message, innerException )
    {
    }

    public DuplicateMigrationException( string message, long id )
    : base( message )
    {
        Id = id;
    }

    public DuplicateMigrationException( string message, long id, Exception innerException )
    : base( message, innerException )
    {
        Id = id;
    }
}