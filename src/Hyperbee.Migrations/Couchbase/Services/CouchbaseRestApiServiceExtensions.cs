using System;
using System.Threading;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Core.Exceptions;

namespace Hyperbee.Migrations.Couchbase.Services;

internal static class CouchbaseRestApiServiceExtensions
{
    public static async Task WaitUntilManagementReadyAsync( this ICouchbaseRestApiService restApi, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitUntilAsync( 
            async token => await restApi.ManagementReadyAsync( token ).ConfigureAwait( false ), 
            timeout, 
            cancellationToken 
        );
    }

    public static async Task WaitUntilBucketHealthyAsync( this ICouchbaseRestApiService restApi, string bucketName, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitUntilAsync( 
            async token => await restApi.BucketHealthyAsync( bucketName, token ).ConfigureAwait( false ), 
            timeout, 
            cancellationToken 
        );
    }

    public static async Task WaitUntilClusterHealthyAsync( this ICouchbaseRestApiService restApi, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitUntilAsync( 
            async token => await restApi.ClusterHealthyAsync( token ).ConfigureAwait( false ), 
            timeout, 
            cancellationToken 
        );
    }

    private static async Task WaitUntilAsync( Func<CancellationToken, Task<bool>> function, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        using var tokenSource = new CancellationTokenSource();
        tokenSource.CancelAfter( timeout );
        var timeoutToken = tokenSource.Token;

        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource( timeoutToken, cancellationToken );
        var token = linkedTokenSource.Token;

        var delay = 100;

        while ( true )
        {
            try
            {
                token.ThrowIfCancellationRequested();

                var result = await function( token );

                if ( result )
                    return;

                // incremental back-off

                await Task.Delay( delay, token );
                delay = Math.Min( delay * 2, 1000 );
            }
            catch ( OperationCanceledException ex )
            {
                if ( timeoutToken.IsCancellationRequested )
                    throw new UnambiguousTimeoutException( $"Timed out after {timeout}.", ex );

                throw new OperationCanceledException( "Operation was cancelled.", cancellationToken );
            }
            catch ( Exception ex )
            {
                throw new CouchbaseException( "An error has occurred, see the inner exception for details.", ex );
            }
        }
    }
}