using System;
using System.Threading;

namespace Hyperbee.Migrations.Wait;

public static class TimeoutTokenSource
{
    // helper to create a token source from a nullable TimeSpan
    public static CancellationTokenSource CreateTokenSource( TimeSpan? timeout )
    {
        var tokenSource = new CancellationTokenSource();

        if ( timeout.HasValue && timeout.Value != TimeSpan.Zero )
            tokenSource.CancelAfter( timeout.Value );

        return tokenSource;
    }
}
