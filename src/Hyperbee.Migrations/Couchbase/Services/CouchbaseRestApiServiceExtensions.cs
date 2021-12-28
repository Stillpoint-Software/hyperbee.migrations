using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hyperbee.Migrations.Couchbase.Wait;

namespace Hyperbee.Migrations.Couchbase.Services;

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

    public static async Task WaitUntilClusterHealthyAsync( this ICouchbaseRestApiService restApi, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync( 
            async token => await restApi.ClusterHealthyAsync( token ).ConfigureAwait( false ), 
            timeout, 
            cancellationToken 
        );
    }
}