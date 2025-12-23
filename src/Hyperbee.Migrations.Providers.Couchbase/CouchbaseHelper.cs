using System;
using System.Linq;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Management.Collections;

namespace Hyperbee.Migrations.Providers.Couchbase;

public sealed record IndexItem( string BucketName, string IndexName, string Statement, bool IsPrimary );

public sealed record ClusterHelper( ICluster Cluster );

public static class ClusterProviderExtensions
{
    public static ClusterHelper Helper( this ICluster cluster ) => new( cluster );

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

    // bucket

    public static async Task<bool> BucketExistsAsync( this ClusterHelper clusterHelper, string bucketName )
    {
        var cluster = clusterHelper.Cluster;
        var buckets = await cluster.Buckets.GetAllBucketsAsync()
            .ConfigureAwait( false );

        return buckets.ContainsKey( Unquote( bucketName ) );
    }

    public static async Task<bool> BucketExistsQueryAsync( this ClusterHelper clusterHelper, string bucketName )
    {
        // Query N1QL for the bucket, collection, or scope.
        //
        // There is a small window after management api creation where an item exists
        // but isn't available to N1QL. This method provides a mechanism for waiting
        // until N1QL is ready to process queries.

        // N1Ql is returning incomplete results when previously shutdown ungracefully
        // this can be fixed by querying for "select * from system:indexes" first.

        await Fixes.SystemQueriesAsync( clusterHelper ).ConfigureAwait( false );

        // N1Ql query the keyspace for the bucket
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:buckets WHERE name = '{Unquote( bucketName )}'"
        ).ConfigureAwait( false );
    }

    // scope

    public static async Task CreateScopeAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        try
        {
            var cluster = clusterHelper.Cluster;
            var bucket = await cluster.BucketAsync( Unquote( bucketName ) )
                .ConfigureAwait( false );

            await bucket.Collections.CreateScopeAsync( scopeName ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            // Log and rethrow to surface the specific error
            Console.WriteLine( $"CreateScopeAsync failed: {ex.Message}" );
            
            // Check if it's an "already exists" error and suppress it
            if ( ex.Message.Contains( "already exists" ) || ex.Message.Contains( "scope already exists" ) )
                return;
                
            throw;
        }
    }

    public static async Task DropScopeAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        var cluster = clusterHelper.Cluster;
        var bucket = await cluster.BucketAsync( Unquote( bucketName ) )
            .ConfigureAwait( false );

        await bucket.Collections.DropScopeAsync( scopeName ).ConfigureAwait( false );
    }

    public static async Task<bool> ScopeExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        try
        {
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
        catch ( Exception ex )
        {
            // Log the exception and return false to trigger fallback behavior
            Console.WriteLine( $"ScopeExistsAsync failed: {ex.Message}" );
            return false;
        }
    }

    public static async Task<bool> ScopeExistsQueryAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        // Query N1QL for the bucket, collection, or scope.
        //
        // There is a small window after management api creation where an item exists
        // but isn't available to N1QL. This method provides a mechanism for waiting
        // until N1QL is ready to process queries.

        // N1Ql is returning incomplete results when previously shutdown ungracefully
        // this can be fixed by querying for "select * from system:indexes" first.

        await Fixes.SystemQueriesAsync( clusterHelper ).ConfigureAwait( false );

        // N1Ql query the keyspace for the scope
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:scopes WHERE `bucket` = '{Unquote( bucketName )}' AND name = '{Unquote( scopeName )}'"
        ).ConfigureAwait( false );
    }

    // collection

    public static async Task CreateCollectionAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        try
        {
            var cluster = clusterHelper.Cluster;
            var bucket = await cluster.BucketAsync( Unquote( bucketName ) )
                .ConfigureAwait( false );

            //      var collectionSpec = new CollectionSpec( Unquote( scopeName ), Unquote( collectionName ) );
            //      await bucket.Collections.CreateCollectionAsync( collectionSpec ).ConfigureAwait( false );

            var settings = CreateCollectionSettings.Default;
            await bucket.Collections.CreateCollectionAsync( Unquote( scopeName ), Unquote( collectionName ), settings ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            // Log and rethrow to surface the specific error
            Console.WriteLine( $"CreateCollectionAsync failed: {ex.Message}" );
            throw;
        }
    }

    public static async Task DropCollectionAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        var cluster = clusterHelper.Cluster;
        var bucket = await cluster.BucketAsync( Unquote( bucketName ) )
            .ConfigureAwait( false );

        //      var collectionSpec = new CollectionSpec( Unquote( scopeName ), Unquote( collectionName ) );
        //      await bucket.Collections.DropCollectionAsync( collectionSpec ).ConfigureAwait( false );

        await bucket.Collections.DropCollectionAsync( Unquote( scopeName ), Unquote( collectionName ) ).ConfigureAwait( false );
    }

    public static async Task<bool> CollectionExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        try
        {
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
        catch ( Exception ex )
        {
            // Log the exception and return false to trigger fallback behavior
            // This prevents hanging on management API calls
            Console.WriteLine( $"CollectionExistsAsync failed: {ex.Message}" );
            return false;
        }
    }

    public static async Task<bool> CollectionExistsQueryAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        try
        {
            // Query N1QL for the bucket, collection, or scope.
            //
            // There is a small window after management api creation where an item exists
            // but isn't available to N1QL. This method provides a mechanism for waiting
            // until N1QL is ready to process queries.

            // N1Ql is returning incomplete results when previously shutdown ungracefully
            // this can be fixed by querying for "select * from system:indexes" first.

            await Fixes.SystemQueriesAsync( clusterHelper ).ConfigureAwait( false );

            // N1Ql query the keyspace for the scope and collection
            return await QueryExistsAsync(
                clusterHelper,
                $"SELECT RAW count(*) FROM system:keyspaces WHERE `bucket` = '{Unquote( bucketName )}' AND `scope` = '{Unquote( scopeName )}' AND name = '{Unquote( collectionName )}'"
            ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            // Log the exception and return false to prevent hanging
            Console.WriteLine( $"CollectionExistsQueryAsync failed: {ex.Message}" );
            return false;
        }
    }

    public static async Task CreatePrimaryCollectionIndexAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        try
        {
            await QueryExecuteAsync(
                clusterHelper,
                $"CREATE PRIMARY INDEX ON `default`:`{Unquote( bucketName )}`.`{Unquote( scopeName )}`.`{Unquote( collectionName )}`"
            ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            // Log and rethrow to surface the specific error
            Console.WriteLine( $"CreatePrimaryCollectionIndexAsync failed: {ex.Message}" );
            throw;
        }
    }

    public static async Task<bool> PrimaryCollectionIndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        try
        {
            return await QueryExistsAsync(
                clusterHelper,
                $"SELECT RAW count(*) FROM system:indexes WHERE bucket_id = '{Unquote( bucketName )}' AND scope_id = '{Unquote( scopeName )}' AND keyspace_id = '{Unquote( collectionName )}' AND is_primary"
            ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            // Log the exception and return false to trigger index creation
            Console.WriteLine( $"PrimaryCollectionIndexExistsAsync failed: {ex.Message}" );
            return false;
        }
    }

    // index

    public static async Task<bool> IndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string indexName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE keyspace_id = '{Unquote( bucketName )}' AND name = '{Unquote( indexName )}'"
        ).ConfigureAwait( false );
    }

    public static async Task<bool> PrimaryIndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string indexName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE keyspace_id = '{Unquote( bucketName )}' AND name = '{Unquote( indexName )}' AND is_primary"
        ).ConfigureAwait( false );
    }

    // query

    internal static async Task QueryExecuteAsync( this ClusterHelper clusterHelper, string statement )
    {
        await clusterHelper.Cluster.QueryAsync<dynamic>( statement )
            .ConfigureAwait( false );
    }

    private static async Task<bool> QueryExistsAsync( this ClusterHelper clusterHelper, string statement )
    {
        var result = await clusterHelper.Cluster.QueryAsync<int>( statement )
            .ConfigureAwait( false );

        await foreach ( var value in result.Rows.ConfigureAwait( false ) )
            return value > 0;

        return false;
    }

    private static class Fixes
    {
        private static bool __systemQueriesFixed;

        internal static async ValueTask SystemQueriesAsync( ClusterHelper clusterHelper )
        {
            // Couchbase 7.0.2.6703
            //
            // N1Ql is returning incomplete results after an ungraceful shutdown.
            // this can be fixed by querying for "select * from system:indexes" first - spooky

            if ( __systemQueriesFixed )
                return;

            await QueryExecuteAsync(
                clusterHelper,
                "SELECT RAW count(*) FROM system:indexes"
            );

            __systemQueriesFixed = true;
        }

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
