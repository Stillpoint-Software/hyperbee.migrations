using System;
using System.Threading;

namespace Hyperbee.Migrations.Couchbase.Wait;

public class TimeoutTokenProvider : IDisposable
{
    public CancellationToken Token { get; } // the combined token
    public CancellationToken CancellationToken { get; } = CancellationToken.None;
    public CancellationToken TimeoutToken { get; } = CancellationToken.None;

    public TimeSpan? Timeout { get; }

    private CancellationTokenSource TimeoutTokenSource { get; }
    private CancellationTokenSource LinkedTokenSource { get; }

    public TimeoutTokenProvider( TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        if ( !timeout.HasValue && cancellationToken == CancellationToken.None )
            return;

        Timeout = timeout;

        if ( Timeout.HasValue )
        {
            TimeoutTokenSource = new CancellationTokenSource();
            TimeoutTokenSource.CancelAfter( Timeout.Value );
            TimeoutToken = TimeoutTokenSource.Token;

            LinkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource( TimeoutToken, cancellationToken );
            Token = LinkedTokenSource.Token;
        }
        else
        {
            // no need to create a linked token if there is no timeout
            Token = cancellationToken;
        }
    }

    protected virtual void Dispose( bool disposing )
    {
        if ( !disposing ) 
            return;

        TimeoutTokenSource?.Dispose();
        LinkedTokenSource?.Dispose();
    }

    public void Dispose()
    {
        Dispose( true );
        GC.SuppressFinalize( this );
    }
}