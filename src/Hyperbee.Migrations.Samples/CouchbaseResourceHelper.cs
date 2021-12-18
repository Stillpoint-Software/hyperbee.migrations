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
    public static async Task CreateBucketsFromResourcesAsync( this ClusterHelper clusterHelper, ILogger logger, string migrationName, string resourceName, TimeSpan waitInterval, int maxAttempts )
    {
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

        foreach ( var bucketSettings in ReadResources( migrationName, resourceName ) )
        {
            if ( await clusterHelper.BucketExistsAsync( bucketSettings.Name ) )
                continue;

            logger?.LogInformation( "Creating bucket `{bucketName}`", bucketSettings.Name );

            await clusterHelper.Cluster.Buckets.CreateBucketAsync( bucketSettings )
                .ConfigureAwait( false );

            await clusterHelper.WaitUntilAsync(
                async () => await clusterHelper.BucketExistsAsync( bucketSettings.Name ),
                waitInterval,
                maxAttempts
            );
        }
    }

    private record IndexItem( string BucketName, string IndexName, string Statement );

    public static async Task CreateIndexesFromResourcesAsync( this ClusterHelper clusterHelper, ILogger logger, string migrationName, params string[] resourceNames )
    {
        static IEnumerable<IndexItem> ReadResources( ClusterHelper clusterHelper, string migrationName, params string[] resourceNames )
        {
            foreach ( var resourceName in resourceNames )
            {
                var json = ResourceHelper.GetResource( migrationName, resourceName );
                var node = JsonNode.Parse( json );
                
                var statements = node!["statements"]!.AsArray()
                    .Select( e => e["statement"]?.ToString() )
                    .Where( x => x != null );

                foreach ( var statement in statements )
                {
                    clusterHelper.ExtractIndexNameAndBucketFromStatement( statement, out var indexName, out var bucketName );
                    yield return new IndexItem( bucketName, indexName, statement );
                }
            }
        }

        var count = 0;

        foreach ( var (bucketName, indexName, statement) in ReadResources( clusterHelper, migrationName, resourceNames ) )
        {
            if ( indexName != null && await clusterHelper.IndexExistsAsync( bucketName, indexName ) )
                continue;

            logger?.LogInformation( "Creating index {count}. {indexName} ON {bucketName}", ++count, indexName ?? "<BUILD>", bucketName ); // empty index name indicates BUILD operation
            await clusterHelper.QueryExecuteAsync( statement );
        }
    }

    private record DocumentItem( string BucketName, string CollectionName, string Id, string Content );

    public static async Task CreateDocumentsFromResourcesAsync( this ClusterHelper clusterHelper, ILogger logger, string migrationName, params string[] resourcePaths )
    {
        static DocumentItem CreateDocumentItem( string resourcePath, JsonNode node, JsonSerializerOptions options )
        {
            var resourceParts = resourcePath.Split( '.', '/' );

            var bucketName = resourceParts[0];
            var collectionName = resourceParts[^1];

            var id = node["id"]!.GetValue<string>();
            var content = node.ToJsonString( options );

            return new DocumentItem( bucketName, collectionName, id, content );
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

        foreach ( var (bucketName, collectionName, id, content) in ReadResources( migrationName, resourcePaths ) )
        {
            logger?.LogInformation( "Upserting `{id}` TO {bucketName}", id, bucketName );

            var bucket = await clusterHelper.Cluster.BucketAsync( bucketName );
            var collection = await bucket.CollectionAsync( collectionName );
            await collection.UpsertAsync( id, content, x => x.Transcoder( new RawStringTranscoder() ) );
        }
    }
}