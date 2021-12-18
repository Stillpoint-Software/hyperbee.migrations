using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Hyperbee.Migrations.Couchbase;
using Hyperbee.Migrations.Samples.Resources;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Samples;

public static class CouchbaseResourceHelper
{
    private record IndexItem( string BucketName, string IndexName, string Statement );

    public static async Task CreateIndexesFromResourcesAsync( IClusterProvider clusterProvider, ILogger logger, string migrationName, params string[] resourceNames )
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

        var cluster = await clusterProvider.GetClusterAsync();
        var clusterHelper = cluster.Helper();
        var count = 0;

        foreach ( var (bucketName, indexName, statement) in ReadResources( clusterHelper, migrationName, resourceNames ) )
        {
            if ( indexName != null && await clusterHelper.IndexExistsAsync( bucketName, indexName ) )
                continue;

            logger?.LogInformation( "Creating index {count}. {indexName} ON {bucketName}", ++count, indexName ?? "<BUILD>", bucketName ); // empty index name indicates BUILD operation
            await clusterHelper.QueryExecuteAsync( statement );
        }
    }

    public static async Task CreateBucketsFromResourcesAsync( IClusterProvider clusterProvider, ILogger logger, string migrationName, string resourceName, TimeSpan waitInterval, int maxAttempts )
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
        
        var cluster = await clusterProvider.GetClusterAsync();
        var clusterHelper = cluster.Helper();

        foreach ( var bucketSettings in ReadResources( migrationName, resourceName ) )
        {
            if ( await clusterHelper.BucketExistsAsync( bucketSettings.Name ) )
                continue;

            logger?.LogInformation( "Creating bucket `{bucketName}`", bucketSettings.Name );

            await cluster.Buckets.CreateBucketAsync( bucketSettings )
                .ConfigureAwait( false );

            await clusterHelper.WaitUntilAsync(
                async () => await clusterHelper.BucketExistsAsync( bucketSettings.Name ),
                waitInterval,
                maxAttempts
            );
        }
    }

    private record BucketItem( string BucketName, string CollectionName, string Id, string Content );

    public static async Task CreateDataFromResourcesAsync( IClusterProvider clusterProvider, ILogger logger, string migrationName, params string[] resourcePaths )
    {
        static BucketItem ToRecord( string resourcePath, JsonNode node, JsonSerializerOptions options )
        {
            var resourceParts = resourcePath.Split( '.', '/' );

            var bucketName = resourceParts[0];
            var collectionName = resourceParts[^1];

            var id = node["id"]!.GetValue<string>();
            var content = node.ToJsonString( options );

            return new BucketItem( bucketName, collectionName, id, content );
        }

        static IEnumerable<BucketItem> ReadResources( string migrationName, params string[] resourcePaths )
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
                            yield return ToRecord( resourcePath, node, options );
                            break;
                        }
                        case JsonArray:
                        {
                            foreach ( var item in node.AsArray() )
                                yield return ToRecord( resourcePath, item, options );
                            break;
                        }
                    }
                }
            }
        }

        var cluster = await clusterProvider.GetClusterAsync();

        foreach ( var (bucketName, collectionName, id, content) in ReadResources( migrationName, resourcePaths ) )
        {
            logger?.LogInformation( "Upserting `{id}` TO {bucketName}", id, bucketName );

            var bucket = await cluster.BucketAsync( bucketName );
            var collection = await bucket.CollectionAsync( collectionName );
            await collection.UpsertAsync( id, content, x => x.Transcoder( new RawStringTranscoder() ) );
        }
    }
}