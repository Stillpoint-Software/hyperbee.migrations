using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase.Resources;

public class CouchbaseResourceRunner<TMigration>
    where TMigration : Migration
{
    private const string DefaultName = "_default";

    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger _logger;

    public WaitSettings WaitSettings { get; set; }

    public CouchbaseResourceRunner( IClusterProvider clusterProvider, ILogger<TMigration> logger )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
    }

    public async Task CreateBucketsFromAsync( string resourceName )
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
        var waitSettings = WaitSettings ?? new WaitSettings( TimeSpan.Zero, 0 );

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync();

        foreach ( var bucketSettings in ReadResources( migrationName, resourceName ) )
            await CreateBucketAsync( clusterHelper, bucketSettings, waitSettings, _logger );
    }

    public async Task CreateStatementsFromAsync( params string[] resourceNames )
    {
        // resourceName => name/bucket/statement-resource.json

        static IEnumerable<StatementItem> ReadResources( string migrationName, params string[] resourceNames )
        {
            foreach ( var resourceName in resourceNames )
            {
                var json = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceName}" );
                var node = JsonNode.Parse( json );

                var statements = node!["statements"]!.AsArray()
                    .Select( e => e["statement"]?.ToString() )
                    .Where( x => x != null );

                var parser = new StatementParser();

                foreach ( var statement in statements )
                    yield return parser.ParseStatement( statement );
            }
        }

        ThrowIfNoResourceLocationFor();

        var migrationName = Migration.VersionedName<TMigration>();

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync();

        foreach ( var statementItem in ReadResources( migrationName, resourceNames ) )
        {
            switch ( statementItem.StatementType )
            {
                case StatementType.Index:
                case StatementType.PrimaryIndex:
                    await CreateIndexAsync( clusterHelper, statementItem );
                    break;

                case StatementType.Scope:
                    await CreateScopeAsync( clusterHelper, statementItem );
                    break;

                case StatementType.Collection:
                    await CreateCollectionAsync( clusterHelper, statementItem );
                    break;

                case StatementType.Build:
                    await BuildIndexesAsync( clusterHelper, statementItem );
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private record DocumentItem( KeyspaceRef Keyspace, string Id, string Content );

    public async Task CreateDocumentsFromAsync( params string[] resourcePaths )
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

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync();

        foreach ( var (keyspace, id, content) in ReadResources( migrationName, resourcePaths ) )
            await UpsertDocumentAsync( clusterHelper, keyspace, id, content );
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

    private async Task CreateBucketAsync( ClusterHelper clusterHelper, BucketSettings bucketSettings, WaitSettings waitSettings, ILogger logger )
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

    private async Task CreateIndexAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        if ( item.Name != null && await clusterHelper.IndexExistsAsync( item.Keyspace.BucketName, item.Name ) )
            return;

        var kind = item.StatementType == StatementType.PrimaryIndex ? "PRIMARY INDEX" : "INDEX";

        _logger?.LogInformation( "CREATE {kind} {indexName} ON {keyspace}", kind, item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private async Task CreateScopeAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        _logger?.LogInformation( "CREATE SCOPE {indexName} ON {keyspace}", item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private async Task CreateCollectionAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        _logger?.LogInformation( "CREATE COLLECTION {indexName} ON {keyspace}", item.Name, item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private async Task BuildIndexesAsync( ClusterHelper clusterHelper, StatementItem item )
    {
        _logger?.LogInformation( "BUILD INDEX ON {keyspace}", item.Keyspace );
        await clusterHelper.QueryExecuteAsync( item.Statement );
    }

    private async Task UpsertDocumentAsync( ClusterHelper clusterHelper, KeyspaceRef keyspace, string id, string content )
    {
        _logger?.LogInformation( "UPSERT `{id}` TO {bucketName} SCOPE {scopeName} COLLECTION {collectionName}", id, keyspace.BucketName, keyspace.ScopeName, keyspace.CollectionName );

        var bucket = await clusterHelper.Cluster.BucketAsync( keyspace.BucketName );
        var scope = await bucket.ScopeAsync( keyspace.ScopeName );
        var collection = await scope.CollectionAsync( keyspace.CollectionName );
        await collection.UpsertAsync( id, content, x => x.Transcoder( new RawStringTranscoder() ) );
    }
}