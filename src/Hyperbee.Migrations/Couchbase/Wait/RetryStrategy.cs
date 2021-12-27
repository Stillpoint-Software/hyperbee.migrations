using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hyperbee.Migrations.Couchbase.Wait;

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
    {
        Delay = TimeSpan.FromMilliseconds( 100 );
        Backoff = current =>
        {
            var value = current.Add( current );
            return value > TimeSpan.FromSeconds( 1000 ) ? TimeSpan.FromSeconds( 1000 ) : value;
        };
    }
}

public class PauseRetryStrategy : RetryStrategy
{
    public PauseRetryStrategy()
        : this( TimeSpan.FromMilliseconds( 1000 ) )
    {
    }

    public PauseRetryStrategy( TimeSpan pause ) => Delay = pause;
}