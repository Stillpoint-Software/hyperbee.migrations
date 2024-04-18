using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hyperbee.Migrations.Wait;

public class RetryStrategy
{
    public TimeSpan Delay { get; set; }
    public Func<TimeSpan, TimeSpan> Backoff { get; init; }
    public Action<RetryStrategy> WaitAction { get; init; } // provides hook for logging etc.

    public async Task WaitAsync( CancellationToken cancellationToken = default )
    {
        WaitAction?.Invoke( this );

        await Task.Delay( Delay, cancellationToken ).ConfigureAwait( false );

        if ( Backoff != null )
            Delay = Backoff( Delay );
    }
}

public class BackoffRetryStrategy : RetryStrategy
{
    public BackoffRetryStrategy()
        : this( TimeSpan.FromMilliseconds( 100 ), TimeSpan.FromSeconds( 120 ) )
    {
    }

    public BackoffRetryStrategy( TimeSpan firstDelay, TimeSpan maxDelay )
    {
        Delay = firstDelay;
        Backoff = current =>
        {
            var value = current.Add( current ); // double the wait
            return value > maxDelay ? maxDelay : value;
        };
    }
}

public class PauseRetryStrategy : RetryStrategy
{
    public PauseRetryStrategy()
        : this( TimeSpan.FromMilliseconds( 1000 ) )
    {
    }

    public PauseRetryStrategy( TimeSpan delay ) => Delay = delay;
}