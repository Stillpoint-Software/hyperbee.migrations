using System.Text.Json.Nodes;
using Aerospike.Client;
using Hyperbee.Migrations.Providers.Aerospike.Extensions;
using Hyperbee.Migrations.Providers.Aerospike.Parsers;
using Hyperbee.Migrations.Resources;
using Hyperbee.Migrations.Wait;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Aerospike.Resources;

public class AerospikeResourceRunner<TMigration>
    where TMigration : Migration
{
    private readonly IAsyncClient _asyncClient;
    private readonly IAerospikeClient _client;
    private readonly ILogger _logger;

    public AerospikeResourceRunner(
        IAsyncClient asyncClient,
        IAerospikeClient client,
        ILogger<TMigration> logger )
    {
        _asyncClient = asyncClient;
        _client = client;
        _logger = logger;
    }

    // statements

    public Task StatementsFromAsync( string resourceName, CancellationToken cancellationToken = default )
    {
        return StatementsFromAsync( new[] { resourceName }, default, cancellationToken );
    }

    public Task StatementsFromAsync( string resourceName, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        return StatementsFromAsync( new[] { resourceName }, timeout, cancellationToken );
    }

    public Task StatementsFromAsync( string[] resourceNames, CancellationToken cancellationToken = default )
    {
        return StatementsFromAsync( resourceNames, default, cancellationToken );
    }

    public async Task StatementsFromAsync( string[] resourceNames, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        static IEnumerable<AerospikeStatementItem> ReadResources( string migrationName, params string[] resourceNames )
        {
            foreach ( var resourceName in resourceNames )
            {
                var json = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceName}" );
                var node = JsonNode.Parse( json );

                var statements = node!["statements"]!
                    .AsArray()
                    .Select( x => x["statement"]?.ToString() )
                    .Where( x => x != null );

                var parser = new AerospikeStatementParser();

                foreach ( var statement in statements )
                    yield return parser.ParseStatement( statement );
            }
        }

        ThrowIfNoResourceLocationFor();

        var migrationName = Migration.VersionedName<TMigration>();

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        foreach ( var statementItem in ReadResources( migrationName, resourceNames ) )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            switch ( statementItem.StatementType )
            {
                case AerospikeStatementType.CreateIndex:
                    await CreateIndexFromStatementAsync( statementItem, operationCancelToken ).ConfigureAwait( false );
                    break;

                case AerospikeStatementType.DropIndex:
                    await DropIndexFromStatementAsync( statementItem ).ConfigureAwait( false );
                    break;

                case AerospikeStatementType.CreateSet:
                    _logger?.LogInformation( "CREATE SET {namespace}.{set} (Aerospike creates sets implicitly on first write)", statementItem.Namespace, statementItem.SetName );
                    break;

                case AerospikeStatementType.Insert:
                    _logger?.LogInformation( "INSERT INTO {namespace}.{set} (use DocumentsFromAsync for document insertion)", statementItem.Namespace, statementItem.SetName );
                    break;

                case AerospikeStatementType.Delete:
                    _logger?.LogInformation( "DELETE FROM {namespace}.{set} (use direct client operations for record deletion)", statementItem.Namespace, statementItem.SetName );
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    // documents

    private record DocumentItem( string Namespace, string SetName, string Id, Dictionary<string, object> Bins );

    public Task DocumentsFromAsync( string resourcePath, CancellationToken cancellationToken = default )
    {
        return DocumentsFromAsync( new[] { resourcePath }, default, cancellationToken );
    }

    public Task DocumentsFromAsync( string resourcePath, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        return DocumentsFromAsync( new[] { resourcePath }, timeout, cancellationToken );
    }

    public Task DocumentsFromAsync( string[] resourcePaths, CancellationToken cancellationToken = default )
    {
        return DocumentsFromAsync( resourcePaths, default, cancellationToken );
    }

    public async Task DocumentsFromAsync( string[] resourcePaths, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        static DocumentItem CreateDocumentItem( string resourcePath, JsonNode node )
        {
            var resourceParts = resourcePath.Split( '.', '/' );
            var count = resourceParts.Length;

            if ( count < 2 || count > 2 )
                throw new ArgumentException( "Invalid resource path. Path must be in the form 'namespace/set'.", nameof( resourcePaths ) );

            var namespaceName = resourceParts[0];
            var setName = resourceParts[1];

            // extract id from the document
            var id = node["id"]?.GetValue<string>()
                  ?? node["PK"]?.GetValue<string>()
                  ?? throw new ArgumentException( "Document must contain an 'id' or 'PK' field." );

            // convert JSON properties to bins
            var bins = new Dictionary<string, object>();

            foreach ( var property in node.AsObject() )
            {
                if ( property.Key is "id" or "PK" )
                    continue;

                bins[property.Key] = property.Value switch
                {
                    null => null,
                    JsonValue value when value.TryGetValue<long>( out var l ) => l,
                    JsonValue value when value.TryGetValue<double>( out var d ) => d,
                    JsonValue value when value.TryGetValue<bool>( out var b ) => b,
                    JsonValue value when value.TryGetValue<string>( out var s ) => s,
                    _ => property.Value.ToJsonString()
                };
            }

            return new DocumentItem( namespaceName, setName, id, bins );
        }

        IEnumerable<DocumentItem> ReadResources( string migrationName, params string[] resourcePaths )
        {
            foreach ( var resourcePath in resourcePaths )
            {
                var resourcePrefix = ResourceHelper.GetResourceName<TMigration>( $"{migrationName}.{resourcePath}." );
                var resourceNames = ResourceHelper.GetResourceNames<TMigration>().Where( x => x.StartsWith( resourcePrefix, StringComparison.OrdinalIgnoreCase ) );

                foreach ( var resourceName in resourceNames )
                {
                    var json = ResourceHelper.GetResource<TMigration>( resourceName, fullyQualified: true );
                    var node = JsonNode.Parse( json );

                    switch ( node )
                    {
                        case JsonObject:
                            yield return CreateDocumentItem( resourcePath, node );
                            break;

                        case JsonArray:
                            foreach ( var item in node.AsArray() )
                                yield return CreateDocumentItem( resourcePath, item );
                            break;
                    }
                }
            }
        }

        ThrowIfNoResourceLocationFor();
        var migrationName = Migration.VersionedName<TMigration>();

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        foreach ( var (namespaceName, setName, id, bins) in ReadResources( migrationName, resourcePaths ) )
        {
            operationCancelToken.ThrowIfCancellationRequested();
            await UpsertDocumentAsync( namespaceName, setName, id, bins ).ConfigureAwait( false );
        }
    }

    // imperative operations (public — for migrations that prefer code over DSL)

    public Task CreateIndexAsync(
        string ns,
        string setName,
        string indexName,
        string binName,
        IndexType indexType,
        IndexCreateMode mode = IndexCreateMode.Missing,
        bool waitReady = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default )
    {
        _logger?.LogInformation( "CREATE INDEX {indexName} ON {namespace}.{set} ({bin}) {type}",
            indexName, ns, setName, binName, indexType );

        return _client.CreateIndexAsync( ns, setName, indexName, binName, indexType, mode, waitReady, timeout, cancellationToken );
    }

    public Task<bool> IndexExistsAsync( string ns, string indexName )
    {
        return _client.IndexExistsAsync( ns, indexName );
    }

    public Task DropIndexAsync( string ns, string setName, string indexName )
    {
        _logger?.LogInformation( "DROP INDEX {namespace} {indexName}", ns, indexName );
        return _client.DropIndexAsync( ns, setName, indexName );
    }

    public Task UpsertDocumentAsync(
        string ns,
        string setName,
        string id,
        IReadOnlyDictionary<string, object> bins,
        CancellationToken cancellationToken = default )
    {
        _logger?.LogInformation( "UPSERT '{id}' TO {namespace}.{set}", id, ns, setName );
        return _asyncClient.UpsertAsync( ns, setName, id, bins, cancellationToken );
    }

    // DSL operations

    private async Task CreateIndexFromStatementAsync( AerospikeStatementItem item, CancellationToken cancellationToken )
    {
        var aerospikeIndexType = item.IndexType switch
        {
            AerospikeIndexType.String or AerospikeIndexType.Default => IndexType.STRING,
            AerospikeIndexType.Numeric => IndexType.NUMERIC,
            AerospikeIndexType.Geo2DSphere => IndexType.GEO2DSPHERE,
            _ => IndexType.STRING
        };

        var mode = item.Recreate ? IndexCreateMode.Recreate : IndexCreateMode.Missing;

        if ( mode == IndexCreateMode.Recreate )
            _logger?.LogInformation( "@RECREATE index '{indexName}'", item.IndexName );

        await CreateIndexAsync(
            item.Namespace, item.SetName, item.IndexName, item.BinName, aerospikeIndexType,
            mode, item.WaitReady, timeout: null, cancellationToken ).ConfigureAwait( false );
    }

    private Task DropIndexFromStatementAsync( AerospikeStatementItem item )
    {
        return DropIndexAsync( item.Namespace, item.SetName, item.IndexName );
    }

    private Task UpsertDocumentAsync( string ns, string setName, string id, Dictionary<string, object> bins )
    {
        return UpsertDocumentAsync( ns, setName, id, (IReadOnlyDictionary<string, object>) bins );
    }

    private static void ThrowIfNoResourceLocationFor()
    {
        var exists = typeof( TMigration )
            .Assembly
            .GetCustomAttributes( typeof( ResourceLocationAttribute ), false )
            .Cast<ResourceLocationAttribute>()
            .Any();

        if ( !exists )
            throw new NotSupportedException( $"Missing required assembly attribute: {nameof( ResourceLocationAttribute )}." );
    }
}
