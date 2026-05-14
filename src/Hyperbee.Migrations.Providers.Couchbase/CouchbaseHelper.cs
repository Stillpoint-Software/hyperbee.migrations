using System;
using System.Linq;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Management.Collections;
using Couchbase.Management.Query;

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
        // Couchbase's bucket-level management API (POST /pools/default/buckets/<bucket>/scopes)
        // is not strictly ready immediately after WaitUntilBucketHealthy succeeds -- the
        // bucket reports healthy state from the cluster's perspective before its per-bucket
        // management endpoints stabilize. Retry transient failures briefly.
        var cluster = clusterHelper.Cluster;
        var bucket = await cluster.BucketAsync( Unquote( bucketName ) )
            .ConfigureAwait( false );

        const int maxAttempts = 30; // ~15s upper bound
        for ( var attempt = 1; ; attempt++ )
        {
            try
            {
                await bucket.Collections.CreateScopeAsync( scopeName ).ConfigureAwait( false );
                return;
            }
            catch ( Exception ex ) when ( ex.Message.Contains( "already exists" ) || ex.Message.Contains( "scope already exists" ) )
            {
                return;
            }
            catch ( Exception ) when ( attempt < maxAttempts )
            {
                // Transient management-API failure during bucket warmup;
                // brief backoff then retry.
                await Task.Delay( TimeSpan.FromMilliseconds( 500 ) ).ConfigureAwait( false );
            }
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
        catch ( Exception )
        {
            // Treat any probe failure as "not present" to trigger the
            // create-fallback path; the caller will log + handle the real
            // exception when the subsequent create operation surfaces it.
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
        // Retry transient management-API failures during bucket/scope warmup
        // for the same reason as CreateScopeAsync.
        var cluster = clusterHelper.Cluster;
        var bucket = await cluster.BucketAsync( Unquote( bucketName ) )
            .ConfigureAwait( false );

        var settings = CreateCollectionSettings.Default;
        const int maxAttempts = 30; // ~15s upper bound
        for ( var attempt = 1; ; attempt++ )
        {
            try
            {
                await bucket.Collections.CreateCollectionAsync( Unquote( scopeName ), Unquote( collectionName ), settings ).ConfigureAwait( false );
                return;
            }
            catch ( Exception ex ) when ( ex.Message.Contains( "already exists" ) || ex.Message.Contains( "collection already exists" ) )
            {
                return;
            }
            catch ( Exception ) when ( attempt < maxAttempts )
            {
                // Transient management-API failure (e.g., parent scope's
                // collection-management endpoint not yet stable); brief
                // backoff then retry.
                await Task.Delay( TimeSpan.FromMilliseconds( 500 ) ).ConfigureAwait( false );
            }
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
        catch ( Exception )
        {
            // Treat management-API probe failure as "not present" to trigger
            // the create-fallback path; prevents hanging on transient
            // management-API outages. The caller will surface the real
            // exception when the subsequent create operation runs.
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
        catch ( Exception )
        {
            // N1QL visibility probe failure -> treat as "not yet visible"
            // and let the WaitUntilAsync loop retry. The caller's WaitHelper
            // surrounds this and will surface a timeout if it persists.
            return false;
        }
    }

    /// <summary>
    /// Probes whether the N1QL planner has materialized the given collection
    /// in its keyspace-to-datastore mapping. <see cref="CollectionExistsQueryAsync"/>
    /// checks <c>system:keyspaces</c> -- a catalog view -- but the planner's
    /// datastore-mapping is a separate metadata cache that can lag the
    /// catalog by 10s of seconds in containerized Couchbase deployments.
    /// </summary>
    /// <remarks>
    /// The signal is: does a query that COMPILES against the keyspace
    /// succeed? <c>SELECT 1 FROM keyspace LIMIT 0</c> is a planner-only
    /// query (no rows scanned) that nonetheless requires the planner to
    /// resolve the keyspace to its datastore. If the planner can compile
    /// this, <c>CREATE PRIMARY INDEX</c> against the same keyspace will
    /// succeed. Returns false on the specific planner-not-ready errors
    /// (Scope/Bucket/Keyspace not found, 12021/12003); rethrows other
    /// exceptions so genuine query failures surface immediately.
    /// </remarks>
    public static async Task<bool> CollectionPlannerReadyAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        try
        {
            await QueryExecuteAsync(
                clusterHelper,
                $"SELECT 1 FROM `default`:`{Unquote( bucketName )}`.`{Unquote( scopeName )}`.`{Unquote( collectionName )}` LIMIT 0"
            ).ConfigureAwait( false );
            return true;
        }
        catch ( Exception ex ) when (
            ex.Message.Contains( "Scope not found" ) ||
            ex.Message.Contains( "Bucket not found" ) ||
            ex.Message.Contains( "Keyspace not found" ) ||
            ex.Message.Contains( "12021" ) ||
            ex.Message.Contains( "12003" ) )
        {
            return false;
        }
    }

    public static async Task CreatePrimaryCollectionIndexAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        // Use the SDK's typed CreatePrimaryIndexAsync with ScopeName +
        // CollectionName options. This routes through the **management
        // REST API** for index creation, NOT through the N1QL planner,
        // bypassing the planner-catalog refresh window entirely. The raw
        // N1QL `CREATE PRIMARY INDEX ON default:bucket.scope.collection`
        // approach this method previously used was blocked by Couchbase
        // Server 7.0.2-community's slow planner-catalog refresh (3+
        // minutes in CI), even though the management API path completes
        // in <1s.
        var options = new CreatePrimaryQueryIndexOptions()
            .ScopeName( Unquote( scopeName ) )
            .CollectionName( Unquote( collectionName ) )
            .IgnoreIfExists( true );

        await clusterHelper.Cluster.QueryIndexes
            .CreatePrimaryIndexAsync( Unquote( bucketName ), options )
            .ConfigureAwait( false );
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
        catch ( Exception )
        {
            // Probe failure -> return false so the caller triggers
            // CreatePrimaryCollectionIndexAsync, which is itself
            // idempotent on "already exists" responses.
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
