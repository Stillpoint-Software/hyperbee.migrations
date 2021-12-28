using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Diagnostics;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Hyperbee.Migrations.Couchbase.Parsers;
using Hyperbee.Migrations.Couchbase.Wait;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase.Resources;

public class CouchbaseResourceRunner<TMigration>
    where TMigration : Migration
{
    private const string DefaultName = "_default";

    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger _logger;

    public CouchbaseResourceRunner( IClusterProvider clusterProvider, ILogger<TMigration> logger )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
    }

    public Task CreateBucketsFromAsync( string resourceName, CancellationToken cancellationToken = default )
    {
        return CreateBucketsFromAsync( resourceName, default, cancellationToken );
    }

    public async Task CreateBucketsFromAsync( string resourceName, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        // resourceName => name/bucket-resource.json

        static IEnumerable<BucketSettings> ReadResources( string migrationName, string resourceName )
        {
            var json = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceName}" );

            var options = new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            return JsonSerializer.Deserialize<IList<BucketSettings>>( json, options );
        }

        ThrowIfNoResourceLocationFor();

        var migrationName = Migration.VersionedName<TMigration>();
        var clusterHelper = await _clusterProvider.GetClusterHelperAsync();

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        foreach ( var bucketSettings in ReadResources( migrationName, resourceName ) )
        {
            operationCancelToken.ThrowIfCancellationRequested();
            await CreateBucketAsync( clusterHelper, bucketSettings, operationCancelToken ).ConfigureAwait( false );
        }
    }

    public Task CreateStatementsFromAsync( string resourceName, CancellationToken cancellationToken = default )
    {
        return CreateStatementsFromAsync( new[] { resourceName }, default, cancellationToken );
    }

    public Task CreateStatementsFromAsync( string resourceName, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        return CreateStatementsFromAsync( new[] { resourceName }, timeout, cancellationToken );
    }

    public Task CreateStatementsFromAsync( string[] resourceNames, CancellationToken cancellationToken = default )
    {
        return CreateStatementsFromAsync( resourceNames, default, cancellationToken );
    }

    public async Task CreateStatementsFromAsync( string[] resourceNames, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        // resourceName => name/bucket/statement-resource.json

        static IEnumerable<StatementItem> ReadResources( string migrationName, params string[] resourceNames )
        {
            foreach ( var resourceName in resourceNames )
            {
                var json = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceName}" );
                var node = JsonNode.Parse( json );

                var statements = node!["statements"]!
                    .AsArray()
                    .Select( x => x["statement"]?.ToString() )
                    .Where( x => x != null );

                var parser = new StatementParser();

                foreach ( var statement in statements )
                    yield return parser.ParseStatement( statement );
            }
        }

        ThrowIfNoResourceLocationFor();

        var migrationName = Migration.VersionedName<TMigration>();

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync();

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        foreach ( var statementItem in ReadResources( migrationName, resourceNames ) )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            switch ( statementItem.StatementType )
            {
                case StatementType.Index:
                case StatementType.PrimaryIndex:
                    await CreateIndexAsync( clusterHelper, statementItem ).ConfigureAwait( false );
                    break;

                case StatementType.Scope:
                    await CreateScopeAsync( clusterHelper, statementItem ).ConfigureAwait( false );
                    break;

                case StatementType.Collection:
                    await CreateCollectionAsync( clusterHelper, statementItem );
                    break;

                case StatementType.Build:
                    await BuildIndexesAsync( clusterHelper, statementItem ).ConfigureAwait( false );
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private record DocumentItem( KeyspaceRef Keyspace, string Id, string Content );

    public Task CreateDocumentsFromAsync( string resourcePath, CancellationToken cancellationToken = default )
    {
        return CreateDocumentsFromAsync( new[] { resourcePath }, default, cancellationToken );
    }

    public Task CreateDocumentsFromAsync( string resourcePath, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        return CreateDocumentsFromAsync( new[] { resourcePath }, timeout, cancellationToken );
    }

    public Task CreateDocumentsFromAsync( string[] resourcePaths, CancellationToken cancellationToken = default )
    {
        return CreateDocumentsFromAsync( resourcePaths, default, cancellationToken );
    }

    public async Task CreateDocumentsFromAsync( string[] resourcePaths, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        // resourcePath => name/bucket[/scope]/collection

        static DocumentItem CreateDocumentItem( string resourcePath, JsonNode node, JsonSerializerOptions options )
        {
            // validate resource path and split in to bucket, scope and collection parts

            var resourceParts = resourcePath.Split( '.', '/' );
            var count = resourceParts.Length;

            if ( count < 2 || count > 4 )
                throw new ArgumentException( "Invalid resource path. Path must be in the form 'name/bucket[/scope]/collection'.", nameof( resourcePaths ) );

            var bucketName = resourceParts[0];
            var scopeName = count == 4 ? resourceParts[^2] : DefaultName;
            var collectionName = resourceParts[^1];

            var keyspace = new KeyspaceRef( default, bucketName, scopeName, collectionName );

            // read document data and extract id

            var id = node["id"]!.GetValue<string>();
            var content = node.ToJsonString( options );

            return new DocumentItem( keyspace, id, content );
        }

        static IEnumerable<DocumentItem> ReadResources( string migrationName, params string[] resourcePaths )
        {
            foreach ( var resourcePath in resourcePaths )
            {
                var resourcePrefix = ResourceHelper.GetResourceName<TMigration>( $"{migrationName}.{resourcePath}." ); // add trailing '.' to ensure StartsWith doesn't find false positives
                var resourceNames = ResourceHelper.GetResourceNames<TMigration>().Where( x => x.StartsWith( resourcePrefix, StringComparison.OrdinalIgnoreCase ) );

                var options = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                foreach ( var resourceName in resourceNames )
                {
                    var json = ResourceHelper.GetResource<TMigration>( resourceName, fullyQualified: true );
                    var node = JsonNode.Parse( json );

                    switch ( node )
                    {
                        case JsonObject:
                        {
                            yield return CreateDocumentItem( resourcePath, node, options );
                            break;
                        }
                        case JsonArray:
                        {
                            foreach ( var item in node.AsArray() )
                                yield return CreateDocumentItem( resourcePath, item, options );
                            break;
                        }
                    }
                }
            }
        }

        ThrowIfNoResourceLocationFor();
        var migrationName = Migration.VersionedName<TMigration>();

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync()
            .ConfigureAwait( false );

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        foreach ( var (keyspace, id, content) in ReadResources( migrationName, resourcePaths ) )
        {
            operationCancelToken.ThrowIfCancellationRequested();
            await UpsertDocumentAsync( clusterHelper, keyspace, id, content ).ConfigureAwait( false );
        }
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

    private async Task CreateBucketAsync( ClusterHelper clusterHelper, BucketSettings bucketSettings, CancellationToken operationCancelToken )
    {
        if ( await clusterHelper.BucketExistsAsync( bucketSettings.Name ) )
            return;

        _logger?.LogInformation( "CREATE BUCKET `{bucketName}`", bucketSettings.Name );

        await clusterHelper.Cluster.Buckets.CreateBucketAsync( bucketSettings )
            .ConfigureAwait( false );

        // wait for the bucket
        //
        const int WaitForExists = 0;
        const int WaitForReady = 1;

        var state = WaitForExists;

        while ( true )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            switch ( state )
            {
                case WaitForExists:
                {
                    if ( !await clusterHelper.BucketExistsAsync( bucketSettings.Name ).ConfigureAwait( false ) )
                    {
                        await Task.Delay( 100, operationCancelToken );
                        continue;
                    }

                    state = WaitForReady;
                    break;
                }

                case WaitForReady:
                {
                    var bucket = await clusterHelper.Cluster.BucketAsync( bucketSettings.Name ).ConfigureAwait( false );
                    var waitOptions = new WaitUntilReadyOptions().CancellationToken( operationCancelToken );
                    await bucket.WaitUntilReadyAsync( TimeSpan.Zero, waitOptions ).ConfigureAwait( false ); // timeout param ignored when cancellation provided
                    return;
                }
            }
        }
    }

    private async Task CreateIndexAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        if ( item.Name != null && await clusterHelper.IndexExistsAsync( item.Keyspace.BucketName, item.Name ).ConfigureAwait( false ) )
            return;

        var kind = item.StatementType == StatementType.PrimaryIndex ? "PRIMARY INDEX" : "INDEX";

        _logger?.LogInformation( "CREATE {kind} {indexName} ON {keyspace}", kind, item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement ).ConfigureAwait( false );
    }

    private async Task CreateScopeAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        _logger?.LogInformation( "CREATE SCOPE {indexName} ON {keyspace}", item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement ).ConfigureAwait( false );
    }

    private async Task CreateCollectionAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        _logger?.LogInformation( "CREATE COLLECTION {indexName} ON {keyspace}", item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement ).ConfigureAwait( false );
    }

    private async Task BuildIndexesAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        _logger?.LogInformation( "BUILD INDEX ON {keyspace}", item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement ).ConfigureAwait( false );
    }

    private async Task UpsertDocumentAsync( ClusterHelper clusterHelper, KeyspaceRef keyspace, string id, string content )
    {
        _logger?.LogInformation( "UPSERT `{id}` TO {bucketName} SCOPE {scopeName} COLLECTION {collectionName}", id, keyspace.BucketName, keyspace.ScopeName, keyspace.CollectionName );

        var bucket = await clusterHelper.Cluster.BucketAsync( keyspace.BucketName ).ConfigureAwait( false );
        var scope = await bucket.ScopeAsync( keyspace.ScopeName ).ConfigureAwait( false );
        var collection = await scope.CollectionAsync( keyspace.CollectionName ).ConfigureAwait( false );
        await collection.UpsertAsync( id, content, x => x.Transcoder( new RawStringTranscoder() ) ).ConfigureAwait( false );
    }
}