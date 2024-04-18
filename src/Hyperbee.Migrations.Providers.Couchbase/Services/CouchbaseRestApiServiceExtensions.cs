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
}