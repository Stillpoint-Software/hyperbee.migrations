using System;
using System.Linq;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Providers.Couchbase;

public sealed record IndexItem( string BucketName, string IndexName, string Statement, bool IsPrimary );

public sealed record ClusterHelper( ICluster Cluster );

public static class ClusterProviderExtensions
{
    public static ClusterHelper Helper( this ICluster cluster ) => new(cluster);

    public static async Task<ClusterHelper> GetClusterHelperAsync( this IClusterProvider clusterProvider )
    {
        var cluster = await clusterProvider.GetClusterAsync()
            .ConfigureAwait( false );

        return cluster.Helper();
    }
}

public static class CouchbaseHelper
{
    public static string Unquote( ReadOnlySpan<char> value ) => value.Trim().Trim( "`'\"" ).ToString();

    public static async Task CreateScopeAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        await QueryExecuteAsync(
            clusterHelper,
            $"CREATE SCOPE `{Unquote( bucketName )}`.`{Unquote( scopeName )}`"
        ).ConfigureAwait( false );
    }

    public static async Task CreateCollectionAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        await QueryExecuteAsync(
            clusterHelper,
            $"CREATE COLLECTION `{Unquote(bucketName)}`.`{Unquote( scopeName )}`.`{Unquote( collectionName )}`"
        ).ConfigureAwait( false );
    }

    public static async Task CreatePrimaryCollectionIndexAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        await QueryExecuteAsync(
            clusterHelper,
            $"CREATE PRIMARY INDEX ON `default`:`{Unquote(bucketName)}`.`{Unquote(scopeName)}`.`{Unquote(collectionName)}`"
        ).ConfigureAwait( false );
    }

    public static async Task<bool> BucketExistsAsync( this ClusterHelper clusterHelper, string bucketName )
    {
        // N1Ql is returning incomplete results when previously shutdown ungracefully
        //      this can be fixed by querying for "select * from system:indexes" first
        //      but it is spooky. let's use an alternative method.
        //
        //return await QueryExistsAsync(
        //    clusterHelper,
        //    $"SELECT RAW count(*) FROM system:buckets WHERE name = '{Unquote(bucketName)}'"
        //);

        var cluster = clusterHelper.Cluster;
        var buckets = await cluster.Buckets.GetAllBucketsAsync()
            .ConfigureAwait( false );

        return buckets.ContainsKey( Unquote(bucketName) );
    }

    public static async Task<bool> ScopeExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        // N1Ql is returning incomplete results when previously shutdown ungracefully
        //      this can be fixed by querying for "select * from system:indexes" first
        //      but it is spooky. let's use an alternative method.
        //
        //return await QueryExistsAsync(
        //    clusterHelper,
        //    $"SELECT RAW count(*) FROM system:scopes WHERE `bucket` = '{Unquote(bucketName)}' AND name = '{Unquote(scopeName)}'"
        //);

        var cluster = clusterHelper.Cluster;
        var buckets = await cluster.Buckets.GetAllBucketsAsync()
            .ConfigureAwait( false );

        bucketName = Unquote( bucketName );

        if ( !buckets.ContainsKey( bucketName ) )
            return false;

        var bucket = await cluster.BucketAsync( bucketName )
            .ConfigureAwait( false );

        var scopes = await bucket.Collections.GetAllScopesAsync().ConfigureAwait( false );

        scopeName = Unquote( scopeName );
        return scopes.Any( x => x.Name == scopeName );
    }

    public static async Task<bool> CollectionExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        // N1Ql is returning incomplete results when previously shutdown ungracefully
        //      this can be fixed by querying for "select * from system:indexes" first
        //      but it is spooky. let's use an alternative method.
        //
        //return await QueryExistsAsync(
        //    clusterHelper,
        //    $"SELECT RAW count(*) FROM system:keyspaces WHERE `bucket` = '{Unquote(bucketName)}' AND `scope` = '{Unquote(scopeName)}' AND name = '{Unquote(collectionName)}'"
        //);

        var cluster = clusterHelper.Cluster;
        var buckets = await cluster.Buckets.GetAllBucketsAsync()
            .ConfigureAwait( false );

        bucketName = Unquote( bucketName );

        if ( !buckets.ContainsKey( bucketName ) )
            return false;

        var bucket = await cluster.BucketAsync( bucketName )
            .ConfigureAwait( false );

        var scopes = await bucket.Collections.GetAllScopesAsync().ConfigureAwait( false );

        scopeName = Unquote( scopeName );
        collectionName = Unquote( collectionName );

        return scopes.Any( x => x.Name == scopeName && x.Collections.Any( y => y.Name == collectionName ) );
    }

    public static async Task<bool> PrimaryCollectionIndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE bucket_id = '{Unquote( bucketName )}' AND scope_id = '{Unquote( scopeName )}' AND keyspace_id = '{Unquote( collectionName )}' AND is_primary"
        ).ConfigureAwait( false );
    }

    public static async Task<bool> IndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string indexName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE keyspace_id = '{Unquote(bucketName)}' AND name = '{Unquote(indexName)}'"
        ).ConfigureAwait( false );
    }

    public static async Task<bool> PrimaryIndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string indexName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE keyspace_id = '{Unquote( bucketName )}' AND name = '{Unquote( indexName )}' AND is_primary"
        ).ConfigureAwait( false );
    }

    internal static async Task QueryExecuteAsync( this ClusterHelper clusterHelper, string statement )
    {
        await clusterHelper.Cluster.QueryAsync<dynamic>( statement )
            .ConfigureAwait( false );
    }

    private static async Task<bool> QueryExistsAsync( this ClusterHelper clusterHelper, string statement )
    {
        var result = await clusterHelper.Cluster.QueryAsync<int>( statement )
            .ConfigureAwait( false );

        return await result.Rows.FirstOrDefaultAsync().ConfigureAwait( false ) > 0;
    }

    private static class Fixes
    {
        /* fixed in 3.2.6.0
         
        internal static async Task<IEnumerable<ScopeSpec>> GetAllScopesAsync( ICouchbaseCollectionManager collections )
        {
            // Couchbase.NetClient 3.2.5.0 is throwing exceptions on success.
            // Extract the status code and response json from the exception
            // context as a temporary workaround.

            try
            {
                return await collections.GetAllScopesAsync()
                    .ConfigureAwait( false );
            }
            catch ( CouchbaseException ex )
            {
                if ( ex.Context is not ManagementErrorContext mc || mc.HttpStatus != HttpStatusCode.OK )
                    throw;

                var json = JObject.Parse( mc.Message );

                return json.SelectToken( "scopes" ).Select( scope => new ScopeSpec( scope["name"].Value<string>() )
                {
                    Collections = scope["collections"].Select( collection =>
                        new CollectionSpec( scope["name"].Value<string>(), collection["name"].Value<string>() )
                        {
                            MaxExpiry = collection["maxTTL"] == null ? null : TimeSpan.FromSeconds( collection["maxTTL"].Value<long>() )
                        }
                    ).ToList()
                } ).ToList();
            }
        }
        */
    }
}