using Hyperbee.Migrations.Providers.MongoDB.Parsers;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// Per-statement classifier that delegates to <see cref="MongoStatementParser"/>
/// and lifts the parser's <see cref="MongoStatementItem"/> into a typed
/// <see cref="ClassifiedStatement"/> consumed by the snapshot strategy and
/// verifier.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the Aerospike + OpenSearch reference shape: thin projection over
/// the existing grammar, default-deny on parser failure with the parser's
/// diagnostic in <see cref="ClassifiedStatement.Detail"/>.
/// </para>
/// <para>
/// MongoDB's statement-form is narrow at v3.0 (6 kinds: CREATE/DROP
/// COLLECTION, CREATE [UNIQUE] INDEX, DROP INDEX, INSERT INTO). View
/// definitions, schema validators, time-series collection settings, and
/// shard-key declarations all happen via the C# driver call-site form
/// rather than statement form. The data-op classifier handles those.
/// </para>
/// </remarks>
public sealed record ClassifiedStatement(
    MongoDBStatementKind Kind,
    string DatabaseName,
    string CollectionName,
    string ObjectName,
    string Body,
    string Detail = null );

public static class MongoDBStatementClassifier
{
    /// <summary>
    /// Classifies a single MongoDB statement. Returns a record with
    /// <see cref="MongoDBStatementKind.Unknown"/> + <paramref name="statement"/>
    /// preserved as <see cref="ClassifiedStatement.Body"/> when the parser
    /// cannot consume the input.
    /// </summary>
    public static ClassifiedStatement Classify( string statement )
    {
        if ( string.IsNullOrWhiteSpace( statement ) )
            return new ClassifiedStatement( MongoDBStatementKind.Unknown, null, null, null, statement ?? "" );

        MongoStatementItem parsed;
        try
        {
            parsed = new MongoStatementParser().ParseStatement( statement );
        }
        catch ( NotSupportedException ex )
        {
            return new ClassifiedStatement(
                Kind: MongoDBStatementKind.Unknown,
                DatabaseName: null,
                CollectionName: null,
                ObjectName: null,
                Body: statement,
                Detail: ex.Message );
        }

        var kind = parsed.StatementType switch
        {
            MongoStatementType.CreateCollection => MongoDBStatementKind.CreateCollection,
            MongoStatementType.DropCollection => MongoDBStatementKind.DropCollection,
            MongoStatementType.CreateIndex => MongoDBStatementKind.CreateIndex,
            MongoStatementType.CreateUniqueIndex => MongoDBStatementKind.CreateUniqueIndex,
            MongoStatementType.DropIndex => MongoDBStatementKind.DropIndex,
            MongoStatementType.Insert => MongoDBStatementKind.Insert,
            _ => MongoDBStatementKind.Unknown
        };

        return new ClassifiedStatement(
            Kind: kind,
            DatabaseName: parsed.DatabaseName,
            CollectionName: parsed.CollectionName,
            ObjectName: parsed.IndexName,
            Body: statement );
    }
}
