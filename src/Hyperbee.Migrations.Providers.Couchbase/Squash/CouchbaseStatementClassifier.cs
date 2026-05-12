using Hyperbee.Migrations.Providers.Couchbase.Parsers;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Per-statement classifier that delegates to the existing
/// <see cref="StatementParser"/> and lifts <see cref="StatementItem"/> into a
/// typed <see cref="ClassifiedStatement"/> consumed by the snapshot strategy
/// and verifier.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the Aerospike + OpenSearch + MongoDB reference shape: thin
/// projection over the existing grammar, default-deny on parser failure with
/// the parser's diagnostic in <see cref="ClassifiedStatement.Detail"/>.
/// </para>
/// <para>
/// The Couchbase <c>StatementParser</c> is partial: it consumes the script-
/// form N1QL the runner emits via <c>statements.json</c> resources, not the
/// full N1QL language. DML shapes (INSERT/UPSERT/UPDATE/DELETE/MERGE) plus
/// arbitrary SELECT/EXPLAIN/ALTER fall through to
/// <see cref="CouchbaseStatementKind.Unknown"/> with the parser's exception
/// message preserved. The data-op classifier recognizes the DML shapes
/// independently via leading-keyword regex so the squash pipeline doesn't
/// require parser support for them.
/// </para>
/// </remarks>
public sealed record ClassifiedStatement(
    CouchbaseStatementKind Kind,
    string Namespace,
    string BucketName,
    string ScopeName,
    string CollectionName,
    string IndexName,
    string Body,
    string Detail = null );

public static class CouchbaseStatementClassifier
{
    /// <summary>
    /// Classifies a single Couchbase statement. Returns a record with
    /// <see cref="CouchbaseStatementKind.Unknown"/> + <paramref name="statement"/>
    /// preserved as <see cref="ClassifiedStatement.Body"/> when the parser
    /// cannot consume the input.
    /// </summary>
    public static ClassifiedStatement Classify( string statement )
    {
        if ( string.IsNullOrWhiteSpace( statement ) )
            return new ClassifiedStatement(
                CouchbaseStatementKind.Unknown,
                null, null, null, null, null,
                statement ?? "" );

        StatementItem parsed;
        try
        {
            parsed = new StatementParser().ParseStatement( statement );
        }
        catch ( NotSupportedException ex )
        {
            return new ClassifiedStatement(
                Kind: CouchbaseStatementKind.Unknown,
                Namespace: null,
                BucketName: null,
                ScopeName: null,
                CollectionName: null,
                IndexName: null,
                Body: statement,
                Detail: ex.Message );
        }
        catch ( ArgumentException ex )
        {
            return new ClassifiedStatement(
                Kind: CouchbaseStatementKind.Unknown,
                Namespace: null,
                BucketName: null,
                ScopeName: null,
                CollectionName: null,
                IndexName: null,
                Body: statement,
                Detail: ex.Message );
        }

        var kind = parsed.StatementType switch
        {
            StatementType.CreateBucket => CouchbaseStatementKind.CreateBucket,
            StatementType.CreateIndex => CouchbaseStatementKind.CreateIndex,
            StatementType.CreatePrimaryIndex => CouchbaseStatementKind.CreatePrimaryIndex,
            StatementType.CreateScope => CouchbaseStatementKind.CreateScope,
            StatementType.CreateCollection => CouchbaseStatementKind.CreateCollection,
            StatementType.DropBucket => CouchbaseStatementKind.DropBucket,
            StatementType.DropScope => CouchbaseStatementKind.DropScope,
            StatementType.DropCollection => CouchbaseStatementKind.DropCollection,
            StatementType.Update => CouchbaseStatementKind.Update,
            StatementType.Build => CouchbaseStatementKind.BuildIndex,
            _ => CouchbaseStatementKind.Unknown
        };

        var keyspace = parsed.Keyspace;

        return new ClassifiedStatement(
            Kind: kind,
            Namespace: keyspace?.Namespace,
            BucketName: keyspace?.BucketName,
            ScopeName: keyspace?.ScopeName,
            CollectionName: keyspace?.CollectionName,
            IndexName: parsed.Name,
            Body: statement );
    }
}
