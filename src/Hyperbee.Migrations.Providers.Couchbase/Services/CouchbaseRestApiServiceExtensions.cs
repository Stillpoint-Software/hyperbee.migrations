using System;
using System.Threading;
using System.Threading.Tasks;
using Hyperbee.Migrations.Wait;

namespace Hyperbee.Migrations.Providers.Couchbase.Services;

internal static class CouchbaseRestApiServiceExtensions
{
    public static async Task WaitUntilManagementReadyAsync( this ICouchbaseRestApiService restApi, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token => await restApi.ManagementReadyAsync( token ).ConfigureAwait( false ),
            timeout,
            cancellationToken
        );
    }

    public static async Task WaitUntilBucketHealthyAsync( this ICouchbaseRestApiService restApi, string bucketName, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token => await restApi.BucketHealthyAsync( bucketName, token ).ConfigureAwait( false ),
            timeout,
            cancellationToken
        );
    }

    public static async Task WaitUntilBucketHealthyAsync( this ICouchbaseRestApiService restApi, string bucketName, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token => await restApi.BucketHealthyAsync( bucketName, token ).ConfigureAwait( false ),
            cancellationToken
        );
    }

    /// <summary>
    /// Waits until the bucket reports healthy AND the terse per-bucket
    /// config advertises KV + N1QL services in the SDK-consumable shape.
    /// The two signals can diverge briefly after bucket creation; opening
    /// the bucket via the SDK before this returns can throw
    /// SocketNotAvailableException.
    /// </summary>
    public static async Task WaitUntilBucketReadyAsync( this ICouchbaseRestApiService restApi, string bucketName, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token =>
            {
                if ( !await restApi.BucketHealthyAsync( bucketName, token ).ConfigureAwait( false ) )
                    return false;
                return await restApi.BucketServicesReadyAsync( bucketName, token ).ConfigureAwait( false );
            },
            timeout,
            cancellationToken
        );
    }

    public static async Task WaitUntilBucketReadyAsync( this ICouchbaseRestApiService restApi, string bucketName, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token =>
            {
                if ( !await restApi.BucketHealthyAsync( bucketName, token ).ConfigureAwait( false ) )
                    return false;
                return await restApi.BucketServicesReadyAsync( bucketName, token ).ConfigureAwait( false );
            },
            cancellationToken
        );
    }

    public static async Task WaitUntilClusterHealthyAsync( this ICouchbaseRestApiService restApi, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token => await restApi.ClusterHealthyAsync( token ).ConfigureAwait( false ),
            timeout,
            cancellationToken
        );
    }

    public static async Task WaitUntilClusterHealthyAsync( this ICouchbaseRestApiService restApi, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token => await restApi.ClusterHealthyAsync( token ).ConfigureAwait( false ),
            cancellationToken
        );
    }

    /// <summary>
    /// Waits until the n1ql query service /admin/ping endpoint responds
    /// with success. The query process can lag the cluster map by a few
    /// seconds; pinging it directly is a stronger signal than relying on
    /// the SDK's cluster bootstrap alone.
    /// </summary>
    public static async Task WaitUntilQueryServiceReadyAsync( this ICouchbaseRestApiService restApi, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token => await restApi.QueryServiceReadyAsync( token ).ConfigureAwait( false ),
            timeout,
            cancellationToken
        );
    }

    /// <summary>
    /// Waits until no cluster task is in the "running" state. Couchbase
    /// rejects CREATE INDEX / ALTER REPLICA while a rebalance is active;
    /// new buckets briefly trigger a post-creation rebalance even on a
    /// single-node cluster. Calling this gate before migrations or test
    /// index-creation avoids the "rebalance in progress" error.
    /// </summary>
    public static async Task WaitUntilClusterIdleAsync( this ICouchbaseRestApiService restApi, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            async token => await restApi.ClusterIdleAsync( token ).ConfigureAwait( false ),
            timeout,
            cancellationToken
        );
    }
}
