namespace Hyperbee.Migrations.Wait;

public static class WaitHelper
{
    public static async Task WaitUntilAsync( Func<CancellationToken, Task<bool>> function, CancellationToken cancellationToken = default )
    {
        await WaitUntilAsync( function, default, default, cancellationToken );
    }

    public static async Task WaitUntilAsync( Func<CancellationToken, Task<bool>> function, TimeSpan timeout, CancellationToken cancellationToken = default )
    {
        await WaitUntilAsync( function, timeout, default, cancellationToken );
    }

    public static async Task WaitUntilAsync( Func<CancellationToken, Task<bool>> function, RetryStrategy retryStrategy, CancellationToken cancellationToken = default )
    {
        await WaitUntilAsync( function, default, retryStrategy, cancellationToken );
    }

    public static async Task WaitUntilAsync( Func<CancellationToken, Task<bool>> function, TimeSpan? timeout, RetryStrategy retryStrategy, CancellationToken cancellationToken = default )
    {
        retryStrategy ??= new BackoffRetryStrategy();

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );

        var operationCancelToken = lts.Token;
        var timeoutToken = tts.Token;

        while ( true )
        {
            try
            {
                operationCancelToken.ThrowIfCancellationRequested();

                var result = await function( operationCancelToken );

                if ( result )
                    return;

                await retryStrategy.WaitAsync( operationCancelToken );
            }
            catch ( OperationCanceledException ex )
            {
                if ( timeoutToken.IsCancellationRequested )
                    throw new RetryTimeoutException( $"Timed out after {timeout}.", ex );

                throw new OperationCanceledException( "Operation was cancelled.", cancellationToken );
            }
            catch ( Exception ex )
            {
                throw new RetryStrategyException( "An error has occurred, see the inner exception for details.", ex );
            }
        }
    }
}

public class RetryStrategyException : Exception
{
    public RetryStrategyException( string message, Exception innerException ) : base( message, innerException ) { }
}

public class RetryTimeoutException : Exception
{
    public RetryTimeoutException( string message, Exception innerException ) : base( message, innerException )
    {
    }
}
