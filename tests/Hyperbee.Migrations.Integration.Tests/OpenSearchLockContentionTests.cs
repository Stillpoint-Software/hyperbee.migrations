//#define INTEGRATIONS
#nullable enable
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Locking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Phase 1 R-24b — lock contention, crash recovery, max-lifetime tests against
// real OpenSearch. Uses FakeTimeProvider so tests run in seconds rather than
// the real-time minutes-to-hours the lock TTLs would otherwise require.
//
// Cluster operations against OpenSearch run in real time (sub-second);
// FakeTimeProvider only controls heartbeat timing, deadline checks, and
// staleness math — those are all on the time provider's clock.

[TestClass]
// Gating (ADR-0031): shared assembly-fixture container, no Docker image build,
// not multi-node. Runs on every PR.
[TestCategory( "Gating" )]
public class OpenSearchLockContentionTests
{
    private static OpenSearchRecordStore BuildStore(
        OpenSearchMigrationOptions options,
        TimeProvider timeProvider )
    {
        var client = OpenSearchTestContainer.Client;
        var steps = new IBootstrapStep[]
        {
            new RestPingStep(),
            new ClusterHealthStep(),
            new LedgerIndexInitStep(),
            new LockIndexInitStep()
        };
        var bootstrapper = new OpenSearchBootstrapper(
            steps, client, options, timeProvider, NullLoggerFactory.Instance );

        return new OpenSearchRecordStore(
            client, bootstrapper, options, timeProvider,
            NullLogger<OpenSearchRecordStore>.Instance );
    }

    private static OpenSearchMigrationOptions UniqueOptions( string testName )
    {
        var slug = $"r24b-{testName.ToLowerInvariant()}-{Guid.NewGuid():n}";
        return new OpenSearchMigrationOptions
        {
            LedgerIndex = $".migrations-{slug}",
            LockIndex = $".migrations-lock-{slug}",
            LockName = $"lock-{slug}",
            // Tight tunings for fast tests — still satisfy the validator
            // (StaleAfter >= 2 * RenewInterval; MaxLifetime > StaleAfter)
            LockRenewInterval = TimeSpan.FromSeconds( 5 ),
            LockStaleAfter = TimeSpan.FromSeconds( 15 ),
            LockMaxLifetime = TimeSpan.FromSeconds( 60 )
        };
    }

    private static async Task CleanupAsync( OpenSearchMigrationOptions options )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        await ll.Indices.DeleteAsync<StringResponse>( options.LedgerIndex );
        await ll.Indices.DeleteAsync<StringResponse>( options.LockIndex );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24b" )]
    public async Task ConcurrentAcquire_OneRunnerWins_OtherGetsLockUnavailable()
    {
        var options = UniqueOptions( nameof( ConcurrentAcquire_OneRunnerWins_OtherGetsLockUnavailable ) );
        var store1 = BuildStore( options, TimeProvider.System );
        var store2 = BuildStore( options, TimeProvider.System );

        try
        {
            await store1.InitializeAsync();

            using var lock1 = await store1.CreateLockAsync();

            // store2 attempts acquire while store1 holds — must surface unavailable
            await Assert.ThrowsExactlyAsync<MigrationLockUnavailableException>(
                async () => await store2.CreateLockAsync() );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24b" )]
    public async Task LockMaxLifetime_FiresLockExpiredCancellationToken()
    {
        // Per R-05 / PM-12: when LockMaxLifetime is reached, the LockHandle's
        // LockExpired CT fires. Advance FakeTimeProvider in small steps and
        // yield between advances so the heartbeat loop's continuation has a
        // chance to run (FakeTimeProvider triggers Task.Delay completions
        // synchronously, but the loop body that follows runs on the task
        // scheduler).

        var options = UniqueOptions( nameof( LockMaxLifetime_FiresLockExpiredCancellationToken ) );
        var fakeTime = new FakeTimeProvider( DateTimeOffset.UtcNow );
        var store = BuildStore( options, fakeTime );

        try
        {
            await store.InitializeAsync();

            using var disposable = await store.CreateLockAsync();
            var handle = (LockHandle) disposable;

            Assert.IsFalse( handle.LockExpired.IsCancellationRequested,
                "LockExpired must not be signaled before max-lifetime" );

            // Advance past LockMaxLifetime in renewal-interval steps, yielding
            // between each so the heartbeat continuation can process.
            // 12 steps * 5s = 60s; LockMaxLifetime = 60s — guaranteed to cross deadline.
            // Add a buffer of 4 more steps to ensure the deadline check runs.
            var maxAttempts = 16;
            for ( var i = 0; i < maxAttempts; i++ )
            {
                fakeTime.Advance( options.LockRenewInterval );

                // Yield enough times for any in-flight renewal HTTP call to
                // complete and the loop to circle back to deadline check.
                for ( var y = 0; y < 10; y++ )
                    await Task.Yield();
                await Task.Delay( 100 );

                if ( handle.LockExpired.IsCancellationRequested )
                    break;
            }

            Assert.IsTrue( handle.LockExpired.IsCancellationRequested,
                "LockExpired must be signaled after LockMaxLifetime is exceeded" );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24b" )]
    public async Task StaleLock_AnotherRunnerTakesOver_AfterStaleAfterElapsed()
    {
        // Crash-recovery scenario. We can't use a live LockHandle here because
        // its heartbeat loop would refresh lastHeartbeat between Advance calls.
        // Instead, write a stale lock document directly via the low-level client,
        // then have store2 attempt acquire — the realtime-GET path inspects
        // lastHeartbeat and CAS-overwrites because the holder is past
        // LockStaleAfter (per R-05 / NF-1).

        var options = UniqueOptions( nameof( StaleLock_AnotherRunnerTakesOver_AfterStaleAfterElapsed ) );
        var store2 = BuildStore( options, TimeProvider.System );
        var ll = OpenSearchTestContainer.LowLevelClient;

        try
        {
            await store2.InitializeAsync();

            // Plant a stale lock document — lastHeartbeat is way past LockStaleAfter
            var staleHeartbeat = DateTimeOffset.UtcNow - options.LockStaleAfter - TimeSpan.FromMinutes( 5 );
            var staleAcquired = staleHeartbeat - TimeSpan.FromMinutes( 1 );
            var staleBody = $$"""
                {
                  "name": "{{options.LockName}}",
                  "owner": "crashed-runner/0",
                  "acquiredAt": "{{staleAcquired:yyyy-MM-ddTHH:mm:ss.fffZ}}",
                  "lastHeartbeat": "{{staleHeartbeat:yyyy-MM-ddTHH:mm:ss.fffZ}}"
                }
                """;

            await ll.IndexAsync<StringResponse>(
                options.LockIndex, options.LockName,
                PostData.String( staleBody ),
                new IndexRequestParameters { Refresh = Refresh.WaitFor } );

            // store2 attempts acquire — should take over via CAS overwrite
            using var lock2 = await store2.CreateLockAsync();

            Assert.IsNotNull( lock2,
                "Second runner must take over a stale lock per R-05 / NF-1 (realtime-GET takeover)" );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }
}
#endif
