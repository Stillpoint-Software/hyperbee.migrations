namespace Hyperbee.Migrations;

public interface IMigrationConventions
{
    string GetRecordId( Migration migration );
}