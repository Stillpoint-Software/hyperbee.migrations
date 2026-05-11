namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// MongoDB statement kinds recognized by <see cref="MongoDBStatementClassifier"/>.
/// Mirrors <see cref="Parsers.MongoStatementType"/> with an additional
/// <see cref="Unknown"/> default-deny state for inputs the parser cannot consume.
/// </summary>
public enum MongoDBStatementKind : byte
{
    Unknown = 0,
    CreateCollection,
    DropCollection,
    CreateIndex,
    CreateUniqueIndex,
    DropIndex,
    Insert
}
