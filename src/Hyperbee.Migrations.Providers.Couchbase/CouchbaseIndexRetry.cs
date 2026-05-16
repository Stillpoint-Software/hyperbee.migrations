using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Core.Exceptions;
using Couchbase.Management.Query;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Couchbase;

/// <summary>
/// Single source of truth for GSI index-DDL resilience: the
/// "rebalance in progress" retry policy plus the post-create
/// wait-until-Online gate.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WithRebalanceRetryAsync"/>: Couchbase rejects CREATE INDEX /
/// ALTER REPLICA with <see cref="InternalServerFailureException"/>
/// "rebalance in progress" while the index service is mid-build from a
/// prior CREATE INDEX or a bucket-creation rebalance. Bounded backoff
/// (60 x 3 s = 3 min -- some CI runners stay in rebalance &gt; 60 s).
/// This policy was previously triplicated and re-tuned independently;
/// it now lives here so the bound changes in exactly one place. It is a
/// last-resort BACKSTOP -- the primary mechanism is to not issue the
/// next CREATE until the prior index is Online (see
/// <see cref="WaitForIndexReadyAsync"/>), which removes the collision
/// at the source.
/// </para>
/// <para>
/// <see cref="WaitForIndexReadyAsync"/> delegates to the SDK's
/// <c>IQueryIndexManager.WatchIndexesAsync</c> (no hand-rolled
/// /indexStatus REST) and is the root-cause fix for the recurring CI
/// "rebalance in progress" failure: serialize index DDL on the actual
/// completion signal of the prior index.
/// </para>
/// </remarks>
internal static class CouchbaseIndexRetry
{
    private const int MaxAttempts = 60;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds( 3 );
    private static readonly TimeSpan DefaultWatchTimeout = TimeSpan.FromMinutes( 3 );

    /// <summary>
    /// Waits until <paramref name="indexName"/> (or the unnamed
    /// <c>#primary</c> when <paramref name="watchPrimaryUnnamed"/> is
    /// true and no name is given) reaches Online, via the SDK
    /// <c>WatchIndexesAsync</c>. Issuing the next CREATE only after this
    /// returns prevents the GSI "rebalance in progress" collision by
    /// construction.
    /// </summary>
    public static async Task WaitForIndexReadyAsync(
        ICluster cluster,
        string bucketName,
        string indexName,
        bool watchPrimaryUnnamed,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default )
    {
        ArgumentNullException.ThrowIfNull( cluster );
        ArgumentException.ThrowIfNullOrWhiteSpace( bucketName );

        var hasName = !string.IsNullOrWhiteSpace( indexName );
        if ( !hasName && !watchPrimaryUnnamed )
            return; // nothing identifiable to watch (defensive; should not happen)

        // The SDK's bucket-level WatchIndexesAsync has no WatchPrimary
        // option; an unnamed primary is watched by its catalog name
        // "#primary". The duration is carried on the options
        // (.Timeout), not as a separate argument.
        var names = new[] { hasName ? indexName : "#primary" };
        var options = new WatchQueryIndexOptions()
            .Timeout( timeout ?? DefaultWatchTimeout )
            .CancellationToken( cancellationToken );

        await cluster.QueryIndexes.WatchIndexesAsync(
            bucketName,
            (IEnumerable<string>) names,
            options ).ConfigureAwait( false );
    }

    /// <summary>
    /// Waits until <paramref name="indexName"/> is gone from the bucket
    /// (the DROP has fully settled in the index service). A DROP INDEX
    /// triggers a GSI index-service rebalance the same way CREATE does;
    /// issuing the next CREATE (e.g. a drop-then-recreate of the same
    /// name) before the drop settles collides with "rebalance in
    /// progress". This is the symmetric counterpart to
    /// <see cref="WaitForIndexReadyAsync"/>.
    /// </summary>
    public static async Task WaitForIndexDroppedAsync(
        ICluster cluster,
        string bucketName,
        string indexName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default )
    {
        ArgumentNullException.ThrowIfNull( cluster );
        ArgumentException.ThrowIfNullOrWhiteSpace( bucketName );
        ArgumentException.ThrowIfNullOrWhiteSpace( indexName );

        var deadline = DateTime.UtcNow + (timeout ?? DefaultWatchTimeout);
        while ( true )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var indexes = await cluster.QueryIndexes
                .GetAllIndexesAsync( bucketName )
                .ConfigureAwait( false );

            var stillPresent = false;
            foreach ( var ix in indexes )
            {
                if ( string.Equals( ix.Name, indexName, StringComparison.Ordinal ) )
                {
                    stillPresent = true;
                    break;
                }
            }

            if ( !stillPresent )
                return;

            if ( DateTime.UtcNow >= deadline )
                throw new System.TimeoutException(
                    $"Index '{indexName}' on bucket '{bucketName}' was not removed within {(timeout ?? DefaultWatchTimeout)}." );

            await Task.Delay( TimeSpan.FromSeconds( 1 ), cancellationToken ).ConfigureAwait( false );
        }
    }

    public static async Task WithRebalanceRetryAsync(
        Func<Task> createIndex,
        ILogger logger = null,
        string label = null )
    {
        ArgumentNullException.ThrowIfNull( createIndex );

        for ( var attempt = 1; ; attempt++ )
        {
            try
            {
                await createIndex().ConfigureAwait( false );
                return;
            }
            catch ( InternalServerFailureException ex )
                when ( ex.Message?.Contains( "rebalance in progress", StringComparison.OrdinalIgnoreCase ) == true
                    && attempt < MaxAttempts )
            {
                logger?.LogInformation(
                    "CREATE {label} blocked by rebalance; retrying ({attempt}/{maxAttempts}).",
                    label ?? "INDEX", attempt, MaxAttempts );
                await Task.Delay( RetryDelay ).ConfigureAwait( false );
            }
        }
    }
}
