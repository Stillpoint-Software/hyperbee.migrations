namespace Hyperbee.Migrations.Providers.MongoDB.Parsers;

public record MongoStatementItem(
    MongoStatementType StatementType,
    string Statement,
    string DatabaseName,
    string CollectionName,
    string IndexName = null,
    string[] FieldNames = null,
    string Expression = null
);
