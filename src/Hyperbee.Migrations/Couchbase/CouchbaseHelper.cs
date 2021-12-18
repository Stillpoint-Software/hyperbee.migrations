using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

public sealed record IndexItem( string BucketName, string IndexName, string Statement, bool IsPrimary );
public sealed record WaitSettings( TimeSpan WaitInterval, int MaxAttempts );

public sealed record ClusterHelper( ICluster Cluster );

public static class ClusterProviderExtensions
{
    public static ClusterHelper Helper( this ICluster cluster ) => new(cluster);

    public static async Task<ClusterHelper> GetClusterHelperAsync( this IClusterProvider clusterProvider )
    {
        var cluster = await clusterProvider.GetClusterAsync();
        return cluster.Helper();
    }
}

public static class CouchbaseHelper
{
    public static async Task CreateScopeAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        await QueryExecuteAsync(
            clusterHelper,
            $"CREATE SCOPE `{bucketName}`.`{scopeName}`"
        );
    }

    public static async Task CreateCollectionAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        await QueryExecuteAsync(
            clusterHelper,
            $"CREATE COLLECTION `{bucketName}`.`{scopeName}`.`{collectionName}`"
        );
    }

    public static async Task CreatePrimaryCollectionIndexAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        await QueryExecuteAsync(
            clusterHelper,
            $"CREATE PRIMARY INDEX ON `default`:`{bucketName}`.`{scopeName}`.`{collectionName}`"
        );
    }

    public static async Task<bool> BucketExistsAsync( this ClusterHelper clusterHelper, string bucketName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:buckets WHERE name = '{bucketName}'"
        );
    }

    public static async Task<bool> ScopeExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:scopes WHERE `bucket` = '{bucketName}' AND name = '{scopeName}'"
        );
    }

    public static async Task<bool> CollectionExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:keyspaces WHERE `bucket` = '{bucketName}' AND `scope` = '{scopeName}' AND name = '{collectionName}'"
        );
    }

    public static async Task<bool> PrimaryCollectionIndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string scopeName, string collectionName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE bucket_id = '{bucketName}' AND scope_id = '{scopeName}' AND keyspace_id = '{collectionName}' AND is_primary"
        );
    }

    public static async Task<bool> IndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string indexName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE keyspace_id = '{bucketName}' AND name = '{indexName}'"
        );
    }

    public static async Task<bool> PrimaryIndexExistsAsync( this ClusterHelper clusterHelper, string bucketName, string indexName )
    {
        return await QueryExistsAsync(
            clusterHelper,
            $"SELECT RAW count(*) FROM system:indexes WHERE keyspace_id = '{bucketName}' AND name = '{indexName}' AND is_primary"
        );
    }

    public static async Task QueryExecuteAsync( this ClusterHelper clusterHelper, string statement )
    {
        await clusterHelper.Cluster.QueryAsync<dynamic>( statement )
            .ConfigureAwait( false );
    }

    private static async Task<bool> QueryExistsAsync( this ClusterHelper clusterHelper, string statement )
    {
        var result = await clusterHelper.Cluster.QueryAsync<int>( statement )
            .ConfigureAwait( false );

        return await result.Rows.FirstOrDefaultAsync() > 0;
    }

    public static IndexItem GetIndexItem( this ClusterHelper clusterHelper, string statement )
    {
        // hackish method to parse out the bucket and index name from an index statement.
        // the regex could do with improvement. trimming, different kinds (or lack of)
        // whitespace, leading and trailing whitespace, \r\n, etc.

        var splitChars = new[] { '\'', '`', ' ', '\t', '(' };

        // CREATE [PRIMARY] INDEX <index> ON <bucket> [..rest] | BUILD INDEX ON <bucket> [..rest]

        var match = Regex.Match( statement, @"^\s*(?:CREATE|BUILD)\s+(?<opt>PRIMARY\s+)?INDEX\s*(?<idx>.*)\s+ON\s*?(?<on>[^\s]+)", RegexOptions.IgnoreCase );

        var isPrimary = match.Groups["opt"].Value
            .StartsWith( "PRIMARY", StringComparison.OrdinalIgnoreCase );

        var indexName = match.Groups["idx"].Value
            .Split( splitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .FirstOrDefault()
            ?.Trim( splitChars );

        var bucketName = match.Groups["on"].Value
            .Split( splitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .FirstOrDefault()
            ?.Trim( splitChars );

        return new IndexItem( bucketName, indexName, statement, isPrimary );
    }

    public static async Task WaitUntilAsync( this ClusterHelper clusterHelper, Func<Task<bool>> condition, WaitSettings settings, ILogger logger = default,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int lineNumber = 0 )
    {
        var (waitInterval, maxAttempts) = settings ?? throw new ArgumentNullException( nameof(settings) );
        await WaitUntilAsync( clusterHelper, condition, waitInterval, maxAttempts, logger, memberName, lineNumber );
    }

    public static async Task WaitUntilAsync( this ClusterHelper clusterHelper, Func<Task<bool>> condition, TimeSpan waitInterval, int maxAttempts, ILogger logger = default,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int lineNumber = 0 )
    {
        while ( maxAttempts-- >= 0 )
        {
            var result = await condition();

            if ( result )
                return;

            logger?.LogInformation( "Waiting..." );
            await Task.Delay( waitInterval );
        }

        throw new MigrationTimeoutException( $"{nameof(WaitUntilAsync)} timed out. Called from member `{memberName}`, line {lineNumber}." );
    }
}