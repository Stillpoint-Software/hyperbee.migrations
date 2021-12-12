using System;
using System.Linq;
using System.Threading.Tasks;
using Couchbase.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

public static class CouchbaseHelper
{
    public static async Task CreateScopeAsync( IClusterProvider clusterProvider, string bucketName, string scopeName )
    {
        await QueryStatementAsync(
            clusterProvider,
            $"CREATE SCOPE `{bucketName}`.`{scopeName}`"
        );
    }

    public static async Task CreateCollectionAsync( IClusterProvider clusterProvider, string bucketName, string scopeName, string collectionName )
    {
        await QueryStatementAsync(
            clusterProvider,
            $"CREATE COLLECTION `{bucketName}`.`{scopeName}`.`{collectionName}`"
        );
    }

    public static async Task CreatePrimaryCollectionIndexAsync( IClusterProvider clusterProvider, string bucketName, string scopeName, string collectionName )
    {
        await QueryStatementAsync(
            clusterProvider,
            $"CREATE PRIMARY INDEX ON `default`:`{bucketName}`.`{scopeName}`.`{collectionName}`"
        );
    }

    public static async Task<bool> BucketExistsAsync( IClusterProvider clusterProvider, string bucketName )
    {
        return await QueryExistsAsync(
            clusterProvider,
            $"SELECT RAW count(*) FROM system:buckets WHERE name = '{bucketName}'"
        );
    }

    public static async Task<bool> ScopeExistsAsync( IClusterProvider clusterProvider, string bucketName, string scopeName )
    {
        return await QueryExistsAsync(
            clusterProvider,
            $"SELECT RAW count(*) FROM system:scopes WHERE `bucket` = '{bucketName}' AND name = '{scopeName}'"
        );
    }

    public static async Task<bool> CollectionExistsAsync( IClusterProvider clusterProvider, string bucketName, string scopeName, string collectionName )
    {
        return await QueryExistsAsync(
            clusterProvider,
            $"SELECT RAW count(*) FROM system:keyspaces WHERE `bucket` = '{bucketName}' AND `scope` = '{scopeName}' AND name = '{collectionName}'"
        );
    }

    public static async Task<bool> PrimaryCollectionIndexExistsAsync( IClusterProvider clusterProvider, string bucketName, string scopeName, string collectionName )
    {
        return await QueryExistsAsync(
            clusterProvider,
            $"SELECT RAW count(*) FROM system:indexes WHERE bucket_id = '{bucketName}' AND scope_id = '{scopeName}' AND keyspace_id = '{collectionName}' AND is_primary"
        );
    }

    private static async Task QueryStatementAsync( IClusterProvider clusterProvider, string statement )
    {
        var cluster = await clusterProvider.GetClusterAsync();

        await cluster.QueryAsync<dynamic>( statement )
            .ConfigureAwait( false );
    }

    private static async Task<bool> QueryExistsAsync( IClusterProvider clusterProvider, string statement )
    {
        var cluster = await clusterProvider.GetClusterAsync();

        var result = await cluster.QueryAsync<int>( statement )
            .ConfigureAwait( false );

        return await result.Rows.FirstOrDefaultAsync() > 0;
    }

    public static async Task WaitUntilAsync( Func<Task<bool>> condition, TimeSpan waitInterval, int maxAttempts, ILogger logger = default )
    {
        while ( maxAttempts-- >= 0 )
        {
            var result = await condition();

            if ( result )
                return;

            logger.LogInformation( "Waiting..." );
            await Task.Delay( waitInterval );
        }
    }
}