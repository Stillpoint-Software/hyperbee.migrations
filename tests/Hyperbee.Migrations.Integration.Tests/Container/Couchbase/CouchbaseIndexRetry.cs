using Couchbase.Core.Exceptions;

namespace Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

/// <summary>
/// Retry helper for GSI CREATE INDEX calls in integration tests.
/// Couchbase rejects CREATE INDEX with InternalServerFailureException
/// "rebalance in progress" while the index service is processing a
/// prior CREATE INDEX or a bucket-creation-triggered cluster
/// rebalance. The condition is transient; this helper retries with
/// backoff so tests don't flake on a race they cannot control.
/// </summary>
internal static class CouchbaseIndexRetry
{
    public static async Task WithRebalanceRetryAsync( Func<Task> create )
    {
        const int maxAttempts = 30;
        for ( var attempt = 1; ; attempt++ )
        {
            try
            {
                await create().ConfigureAwait( false );
                return;
            }
            catch ( InternalServerFailureException ex )
                when ( ex.Message?.Contains( "rebalance in progress", StringComparison.OrdinalIgnoreCase ) == true
                    && attempt < maxAttempts )
            {
                await Task.Delay( TimeSpan.FromSeconds( 2 ) ).ConfigureAwait( false );
            }
        }
    }
}
