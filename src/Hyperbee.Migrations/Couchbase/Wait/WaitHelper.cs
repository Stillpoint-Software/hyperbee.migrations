using System;
using System.Threading;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Core.Exceptions;

namespace Hyperbee.Migrations.Couchbase.Wait;

public static class WaitHelper
{
    public static async Task WaitUntilAsync( Func<CancellationToken, Task<bool>> function, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        await WaitUntilAsync( function, timeout, default, cancellationToken );
    }

    public static async Task WaitUntilAsync( Func<CancellationToken, Task<bool>> function, TimeSpan? timeout, RetryStrategy backoff, CancellationToken cancellationToken = default )
    {
        backoff ??= new BackoffRetryStrategy();

        using var tokenProvider = new TimeoutTokenProvider( timeout, cancellationToken );

        var operationCancelToken = tokenProvider.Token;
        var timeoutToken = tokenProvider.TimeoutToken;

        while ( true )
        {
            try
            {
                operationCancelToken.ThrowIfCancellationRequested();

                var result = await function( operationCancelToken );

                if ( result )
                    return;

                await backoff.WaitAsync( operationCancelToken );
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