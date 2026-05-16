using Couchbase;
using ProviderIndexRetry = Hyperbee.Migrations.Providers.Couchbase.CouchbaseIndexRetry;

namespace Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

/// <summary>
/// Thin test-side facade over the production
/// <see cref="ProviderIndexRetry"/> so integration tests share the exact
/// same GSI index-DDL resilience (rebalance-retry backstop +
/// wait-until-Ready) as the runtime, rather than re-declaring it. The
/// policy lives in the Couchbase provider; this type only keeps the
/// test call-sites stable.
/// </summary>
internal static class CouchbaseIndexRetry
{
    public static Task WithRebalanceRetryAsync( Func<Task> create ) =>
        ProviderIndexRetry.WithRebalanceRetryAsync( create );

    /// <summary>
    /// Create an index (rebalance-retry backstop) then wait until it is
    /// Ready before returning -- the root-cause fix that serializes
    /// fixture index DDL so the next create cannot collide.
    /// </summary>
    public static async Task CreateThenWaitReadyAsync(
        ICluster cluster, string bucketName, string indexName,
        bool watchPrimaryUnnamed, Func<Task> create )
    {
        await ProviderIndexRetry.WithRebalanceRetryAsync( create ).ConfigureAwait( false );
        await ProviderIndexRetry.WaitForIndexReadyAsync(
            cluster, bucketName, indexName, watchPrimaryUnnamed ).ConfigureAwait( false );
    }

    /// <summary>
    /// Drop an index then wait until the DROP has fully settled (the
    /// index is gone) before returning -- so an immediate recreate of
    /// the same name cannot collide with the drop's index-service
    /// rebalance.
    /// </summary>
    public static async Task DropThenWaitGoneAsync(
        ICluster cluster, string bucketName, string indexName, Func<Task> drop )
    {
        await drop().ConfigureAwait( false );
        await ProviderIndexRetry.WaitForIndexDroppedAsync(
            cluster, bucketName, indexName ).ConfigureAwait( false );
    }
}
