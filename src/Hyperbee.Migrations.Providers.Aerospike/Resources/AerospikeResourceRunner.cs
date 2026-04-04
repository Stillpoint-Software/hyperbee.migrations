using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aerospike.Client;
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

                var parser = new AerospikeStatementParser();

                foreach ( var item in node!["statements"]!.AsArray() )
                {
                    var statement = item["statement"]?.ToString();

                    if ( statement == null )
                        continue;

                    var parsed = parser.ParseStatement( statement );

                    // apply directives from JSON properties
                    var recreate = item["recreate"]?.GetValue<bool>() ?? false;
                    var waitReady = item["waitReady"]?.GetValue<bool>() ?? false;

                    yield return parsed with { Recreate = recreate, WaitReady = waitReady };
                }
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
                    await CreateIndexAsync( statementItem, operationCancelToken ).ConfigureAwait( false );
                    break;

                case AerospikeStatementType.DropIndex:
                    await DropIndexAsync( statementItem ).ConfigureAwait( false );
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

    // operations

    private async Task CreateIndexAsync( AerospikeStatementItem item, CancellationToken cancellationToken )
    {
        var indexExists = IndexExists( item.Namespace, item.IndexName );

        if ( indexExists && item.Recreate )
        {
            _logger?.LogInformation( "@RECREATE dropping index '{indexName}'", item.IndexName );
            _client.DropIndex( null, item.Namespace, item.SetName, item.IndexName );
            indexExists = false;
        }

        if ( indexExists )
        {
            _logger?.LogInformation( "skip index '{indexName}' already exists", item.IndexName );
            return;
        }

        var aerospikeIndexType = item.IndexType switch
        {
            AerospikeIndexType.String or AerospikeIndexType.Default => IndexType.STRING,
            AerospikeIndexType.Numeric => IndexType.NUMERIC,
            AerospikeIndexType.Geo2DSphere => IndexType.GEO2DSPHERE,
            _ => IndexType.STRING
        };

        _logger?.LogInformation( "CREATE INDEX {indexName} ON {namespace}.{set} ({bin}) {type}",
            item.IndexName, item.Namespace, item.SetName, item.BinName, item.IndexType );

        try
        {
            var task = _client.CreateIndex( null, item.Namespace, item.SetName, item.IndexName, item.BinName, aerospikeIndexType );

            if ( item.WaitReady )
            {
                await WaitForIndexReadyAsync( item.Namespace, item.IndexName, cancellationToken ).ConfigureAwait( false );
            }
        }
        catch ( AerospikeException ex ) when ( ex.Result == ResultCode.INDEX_ALREADY_EXISTS )
        {
            _logger?.LogInformation( "Index '{indexName}' already exists (race condition)", item.IndexName );
        }
    }

    private async Task DropIndexAsync( AerospikeStatementItem item )
    {
        _logger?.LogInformation( "DROP INDEX {namespace} {indexName}", item.Namespace, item.IndexName );

        try
        {
            _client.DropIndex( null, item.Namespace, item.SetName, item.IndexName );
        }
        catch ( AerospikeException ex ) when ( ex.Result == ResultCode.INDEX_NOTFOUND )
        {
            _logger?.LogInformation( "Index '{indexName}' not found (already dropped)", item.IndexName );
        }
    }

    private async Task UpsertDocumentAsync( string namespaceName, string setName, string id, Dictionary<string, object> bins )
    {
        _logger?.LogInformation( "UPSERT '{id}' TO {namespace}.{set}", id, namespaceName, setName );

        var key = new Key( namespaceName, setName, id );
        var binArray = bins.Select( kvp => new Bin( kvp.Key, kvp.Value ) ).ToArray();

        await _asyncClient.Put( null, CancellationToken.None, key, binArray ).ConfigureAwait( false );
    }

    // helpers

    private bool IndexExists( string namespaceName, string indexName )
    {
        try
        {
            // Query sindex info from the cluster
            var node = _client.Nodes.FirstOrDefault();

            if ( node == null )
                return false;

            var response = Info.Request( node, "sindex/" + namespaceName );

            if ( string.IsNullOrEmpty( response ) )
                return false;

            return response.Contains( indexName );
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForIndexReadyAsync( string namespaceName, string indexName, CancellationToken cancellationToken )
    {
        _logger?.LogInformation( "@WAITREADY waiting for index '{indexName}' to be ready", indexName );

        await WaitHelper.WaitUntilAsync(
            async ct =>
            {
                try
                {
                    var node = _client.Nodes.FirstOrDefault();

                    if ( node == null )
                        return false;

                    // Query sindex info for specific index
                    var response = Info.Request( node, $"sindex/{namespaceName}/{indexName}" );

                    // The index is ready when the response doesn't contain "load_pct" < 100
                    // or when it contains the index info without error
                    return !string.IsNullOrEmpty( response ) && !response.Contains( "FAIL" );
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds( 60 ),
            new BackoffRetryStrategy( TimeSpan.FromMilliseconds( 500 ), TimeSpan.FromSeconds( 5 ) ),
            cancellationToken
        ).ConfigureAwait( false );

        _logger?.LogInformation( "Index '{indexName}' is ready", indexName );
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
