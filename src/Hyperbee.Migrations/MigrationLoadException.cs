namespace Hyperbee.Migrations;

/// <summary>
/// Thrown by <see cref="MigrationRunner"/> at discovery time when migration
/// metadata is structurally invalid — e.g., a squash migration's
/// <see cref="MigrationAttribute.Replaces"/> set names a version that does not
/// correspond to any discovered migration, or self-references the squash's
/// own version (per ADR-0019).
/// </summary>
[Serializable]
public class MigrationLoadException : MigrationException
{
    public MigrationLoadException()
    : base( "Migration load exception." )
    {
    }

    public MigrationLoadException( string message )
    : base( message )
    {
    }

    public MigrationLoadException( string message, Exception innerException )
    : base( message, innerException )
    {
    }
}
