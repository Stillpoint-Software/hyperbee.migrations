namespace Hyperbee.Migrations;

[Serializable]
public class MigrationException : Exception
{
    public MigrationException()
    : base( "Migration exception" )
    {
    }

    public MigrationException( string message )
    : base( message )
    {
    }

    public MigrationException( string message, Exception innerException )
    : base( message, innerException )
    {
    }
}
