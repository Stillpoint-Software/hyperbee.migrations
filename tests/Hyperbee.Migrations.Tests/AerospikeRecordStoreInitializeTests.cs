using Aerospike.Client;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Aerospike;
using Hyperbee.Migrations.Wait;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

[TestClass]
public class AerospikeRecordStoreInitializeTests
{
    // Readiness-gate tests for AerospikeRecordStore.InitializeAsync.
    //
    // The gate exists because _client.Connected flips true as soon as the
    // seed node answers -- before partitions are master-assigned. Without
    // it, the first ledger op on the lock-disabled path can hit a transient
    // cluster error and false-fail the run. The gate probes with a
    // side-effect-free Get of a non-existent sentinel key, retrying ONLY on
    // transient cluster errors (same predicate + 60s bound as
    // CreateLockAsync).

    private static AerospikeMigrationOptions Options() => new()
    {
        Namespace = "test",
        MigrationSet = "SchemaMigrations",
        LockName = "migration_lock",
        LockExpireInterval = TimeSpan.FromSeconds( 60 ),
        LockRenewInterval = TimeSpan.FromSeconds( 30 ),
        LockMaxLifetime = TimeSpan.FromMinutes( 10 )
    };

    private static AerospikeRecordStore CreateStore( IAsyncClient client ) =>
        new( client, Options(), new FakeTimeProvider( DateTimeOffset.UtcNow ),
            NullLogger<AerospikeRecordStore>.Instance );

    [TestMethod]
    public async Task InitializeAsync_throws_when_not_connected()
    {
        var client = Substitute.For<IAsyncClient>();
        client.Connected.Returns( false );

        var store = CreateStore( client );

        await Assert.ThrowsExactlyAsync<MigrationException>(
            () => store.InitializeAsync() );

        // Probe must not run when the client never connected.
        await client.DidNotReceive().Get(
            Arg.Any<Policy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() );
    }

    [TestMethod]
    public async Task InitializeAsync_completes_on_ready_cluster_with_single_probe()
    {
        var client = Substitute.For<IAsyncClient>();
        client.Connected.Returns( true );
        client.Get( Arg.Any<Policy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() )
            .Returns( Task.FromResult<Record>( null ) );

        var store = CreateStore( client );

        await store.InitializeAsync(); // no throw, no hang

        // Healthy path = exactly one probe round-trip. Guards against a
        // latency regression on the common (cluster-already-up) case.
        await client.Received( 1 ).Get(
            Arg.Any<Policy>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<Key>( k => k.ns == "test" && k.setName == "SchemaMigrations" ) );
    }

    [TestMethod]
    public async Task InitializeAsync_absorbs_transient_cluster_window()
    {
        var client = Substitute.For<IAsyncClient>();
        client.Connected.Returns( true );

        var calls = 0;
        client.Get( Arg.Any<Policy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() )
            .Returns( _ =>
            {
                calls++;
                if ( calls == 1 )
                    throw new AerospikeException( ResultCode.INVALID_NODE_ERROR );
                return Task.FromResult<Record>( null );
            } );

        var store = CreateStore( client );

        await store.InitializeAsync(); // transient window absorbed, then succeeds

        await client.Received( 2 ).Get(
            Arg.Any<Policy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() );
    }

    [TestMethod]
    public async Task InitializeAsync_fails_fast_on_nontransient_error()
    {
        // A non-transient AerospikeException (e.g. genuine misconfig) must
        // NOT be retried for the full 60s bound -- it escapes the
        // IsTransientClusterError filter and surfaces immediately.
        var client = Substitute.For<IAsyncClient>();
        client.Connected.Returns( true );
        client.Get( Arg.Any<Policy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() )
            .Throws( new AerospikeException( ResultCode.PARAMETER_ERROR ) );

        var store = CreateStore( client );

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsExactlyAsync<RetryStrategyException>(
            () => store.InitializeAsync() );
        sw.Stop();

        // Fail-fast: well under the 60s transient bound (no hang).
        Assert.IsTrue( sw.Elapsed < TimeSpan.FromSeconds( 10 ),
            $"non-transient error should surface immediately, took {sw.Elapsed}." );
    }
}
