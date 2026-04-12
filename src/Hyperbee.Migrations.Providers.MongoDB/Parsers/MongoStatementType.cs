namespace Hyperbee.Migrations.Providers.MongoDB.Parsers;

public enum MongoStatementType
{
    CreateCollection,
    DropCollection,
    CreateIndex,
    CreateUniqueIndex,
    DropIndex,
    Insert
}
