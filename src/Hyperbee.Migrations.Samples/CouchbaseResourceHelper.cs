using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Couchbase.Core.IO.Transcoders;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Hyperbee.Migrations.Couchbase;
using Hyperbee.Migrations.Samples.Resources;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Samples;

public static class CouchbaseResourceHelper
{
    private const string DefaultName = "_default";

    public static async Task CreateBucketsFromResourcesAsync( this ClusterHelper clusterHelper, ILogger logger, string migrationName, string resourceName )
    {
        await CreateBucketsFromResourcesAsync( clusterHelper, logger, migrationName, default, resourceName );
    }

    public static async Task CreateBucketsFromResourcesAsync( this ClusterHelper clusterHelper, ILogger logger, string migrationName, WaitSettings waitSettings, string resourceName )
    {
        // resourceName => name/bucket-resource.json

        static IEnumerable<BucketSettings> ReadResources( string migrationName, string resourceName )
        {
            var json = ResourceHelper.GetResource( migrationName, resourceName );

            var options = new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            return JsonSerializer.Deserialize<IList<BucketSettings>>( json, options );
        }

        waitSettings ??= new WaitSettings( TimeSpan.Zero, 0 );

        foreach ( var bucketSettings in ReadResources( migrationName, resourceName ) )
            await CreateBucketAsync( clusterHelper, bucketSettings, waitSettings, logger );
    }

    public static async Task CreateStatementsFromResourcesAsync( this ClusterHelper clusterHelper, ILogger logger, string migrationName, params string[] resourceNames )
    {
        // resourceName => name/bucket/statement-resource.json

        static IEnumerable<StatementItem> ReadResources( ClusterHelper clusterHelper, string migrationName, params string[] resourceNames )
        {
            foreach ( var resourceName in resourceNames )
            {
                var json = ResourceHelper.GetResource( migrationName, resourceName );
                var node = JsonNode.Parse( json );

                var statements = node!["statements"]!.AsArray()
                    .Select( e => e["statement"]?.ToString() )
                    .Where( x => x != null );

                var parser = new StatementParser();

                foreach ( var statement in statements )
                    yield return parser.ParseStatement( statement );
            }
        }

        foreach ( var statementItem in ReadResources( clusterHelper, migrationName, resourceNames ) )
        {
            switch(statementItem.StatementType)
            {
                case StatementType.Index:
                case StatementType.PrimaryIndex:
                    await CreateIndexAsync( clusterHelper, statementItem, logger );
                    break;

                case StatementType.Scope:
                    await CreateScopeAsync( clusterHelper, statementItem, logger );
                    break;

                case StatementType.Collection:
                    await CreateCollectionAsync( clusterHelper, statementItem, logger );
                    break;

                case StatementType.Build:
                    await BuildIndexesAsync( clusterHelper, statementItem, logger );
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private record DocumentItem( KeyspaceRef Keyspace, string Id, string Content );

    public static async Task CreateDocumentsFromResourcesAsync( this ClusterHelper clusterHelper, ILogger logger, string migrationName, params string[] resourcePaths )
    {
        // resourcePath => name/bucket[/scope]/collection

        static DocumentItem CreateDocumentItem( string resourcePath, JsonNode node, JsonSerializerOptions options )
        {
            // validate resource path and split in to bucket, scope and collection parts

            var resourceParts = resourcePath.Split( '.', '/' );
            var count = resourceParts.Length;

            if ( count < 2 || count > 4 )
                throw new ArgumentException( "Invalid resource path. Path must be in the form 'name/bucket[/scope]/collection'.", nameof(resourcePaths) );

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
                var resourcePrefix = ResourceHelper.GetResourceName( migrationName, resourcePath ) + "."; // add trailing '.' to ensure StartsWith doesn't find false positives
                var resourceNames = ResourceHelper.GetManifestResourceNames().Where( x => x.StartsWith( resourcePrefix, StringComparison.OrdinalIgnoreCase ) );

                var options = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                foreach ( var resourceName in resourceNames )
                {
                    var json = ResourceHelper.GetResource( resourceName );
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

        foreach ( var (keyspace, id, content) in ReadResources( migrationName, resourcePaths ) )
            await UpsertDocumentAsync( clusterHelper, keyspace, id, content, logger );
    }

    private static async Task CreateBucketAsync( ClusterHelper clusterHelper, BucketSettings bucketSettings, WaitSettings waitSettings, ILogger logger )
    {
        if ( await clusterHelper.BucketExistsAsync( bucketSettings.Name ) )
            return;

        logger?.LogInformation( "CREATE BUCKET `{bucketName}`", bucketSettings.Name );

        await clusterHelper.Cluster.Buckets.CreateBucketAsync( bucketSettings )
            .ConfigureAwait( false );

        if ( waitSettings.WaitInterval == TimeSpan.Zero || waitSettings.MaxAttempts <= 0 )
            return;

        await clusterHelper.WaitUntilAsync(
            async () => await clusterHelper.BucketExistsAsync( bucketSettings.Name ),
            waitSettings.WaitInterval,
            waitSettings.MaxAttempts
        );
    }

    private static async Task CreateIndexAsync( ClusterHelper clusterHelper, StatementItem item, ILogger logger )
    {
        if ( item.Name != null && await clusterHelper.IndexExistsAsync( item.Keyspace.BucketName, item.Name ) )
            return;

        var kind = item.StatementType == StatementType.PrimaryIndex ? "PRIMARY INDEX" : "INDEX";

        logger?.LogInformation( "CREATE {kind} {indexName} ON {keyspace}", kind, item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private static async Task CreateScopeAsync( ClusterHelper clusterHelper, StatementItem item, ILogger logger )
    {
        logger?.LogInformation( "CREATE SCOPE {indexName} ON {keyspace}", item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private static async Task CreateCollectionAsync( ClusterHelper clusterHelper, StatementItem item, ILogger logger )
    {
        logger?.LogInformation( "CREATE COLLECTION {indexName} ON {keyspace}", item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private static async Task BuildIndexesAsync( ClusterHelper clusterHelper, StatementItem item, ILogger logger )
    {
        logger?.LogInformation( "BUILD INDEX ON {keyspace}", item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private static async Task UpsertDocumentAsync( ClusterHelper clusterHelper, KeyspaceRef keyspace, string id, string content, ILogger logger )
    {
        logger?.LogInformation( "Upserting `{id}` TO {bucketName} SCOPE {scopeName} COLLECTION {collectionName}", id, keyspace.BucketName, keyspace.ScopeName, keyspace.CollectionName );

        var bucket = await clusterHelper.Cluster.BucketAsync( keyspace.BucketName );
        var scope = await bucket.ScopeAsync( keyspace.ScopeName );
        var collection = await scope.CollectionAsync( keyspace.CollectionName );
        await collection.UpsertAsync( id, content, x => x.Transcoder( new RawStringTranscoder() ) );
    }
}

