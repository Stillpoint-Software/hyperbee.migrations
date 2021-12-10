using System;

namespace Hyperbee.Migrations;

public class MigrationConventions
{
    public MigrationConventions()
    {
        TypeIsMigration = MigrationHelper.IsMigration;
        MigrationDocumentId = MigrationHelper.GetMigrationDocumentId;
    }

    public Func<Type, bool> TypeIsMigration { get; set; }
    public Func<Migration, char, string> MigrationDocumentId { get; set; }
}
