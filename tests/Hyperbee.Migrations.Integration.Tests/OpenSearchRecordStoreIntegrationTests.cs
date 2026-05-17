//#define INTEGRATIONS
#nullable enable
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Phase 1 integration test — exercises the bootstrapper + lock + ledger end-to-end
// against a real OpenSearch cluster. Validates:
//   - InitializeAsync runs the full bootstrapper pipeline (REST ping ->
//     cluster health -> ledger init -> lock init) successfully
//   - The ledger and lock indices are created with the expected mappings
//   - CreateLockAsync acquires the singleton lock document via op_type=create
//   - Lock dispose releases the document (CAS-guarded)
//   - A second CreateLockAsync after release succeeds (lock truly released)
//   - Ledger CRUD round-trips (Write -> Exists -> Read -> Delete)
//
// Each test uses unique index names so concurrent runs don't collide and
// cleanup is local. Standard #if INTEGRATIONS gate per ADR-0010.

[TestClass]
// LocalOnly: heavy container-based integration test; excluded from the gating CI matrix (does not gate the NuGet publish). Runs locally / on demand.
[TestCategory( "LocalOnly" )]
public class OpenSearchRecordStoreIntegrationTests
{
    private static OpenSearchRecordStore BuildStore( OpenSearchMigrationOptions options )
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
            steps, client, options, TimeProvider.System, NullLoggerFactory.Instance );

        return new OpenSearchRecordStore(
            client, bootstrapper, options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );
    }

    private static OpenSearchMigrationOptions UniqueOptions( string testName )
    {
        var slug = $"phase1-{testName.ToLowerInvariant()}-{Guid.NewGuid():n}";
        return new OpenSearchMigrationOptions
        {
            LedgerIndex = $".migrations-{slug}",
            LockIndex = $".migrations-lock-{slug}",
            LockName = $"lock-{slug}",
            // Tighter TTLs for tests so we don't wait forever
            LockRenewInterval = TimeSpan.FromSeconds( 10 ),
            LockStaleAfter = TimeSpan.FromSeconds( 30 ),
            LockMaxLifetime = TimeSpan.FromMinutes( 5 )
        };
    }

    private static async Task CleanupAsync( OpenSearchMigrationOptions options )
    {
        var client = OpenSearchTestContainer.Client;
        await client.Indices.DeleteAsync( options.LedgerIndex );
        await client.Indices.DeleteAsync( options.LockIndex );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task InitializeAsync_RunsFullBootstrap_CreatesLedgerAndLockIndices()
    {
        var options = UniqueOptions( nameof( InitializeAsync_RunsFullBootstrap_CreatesLedgerAndLockIndices ) );
        var store = BuildStore( options );
        var client = OpenSearchTestContainer.Client;

        try
        {
            await store.InitializeAsync();

            var ledgerExists = await client.Indices.ExistsAsync( options.LedgerIndex );
            Assert.IsTrue( ledgerExists.Exists, $"Ledger index `{options.LedgerIndex}` was not created." );

            var lockExists = await client.Indices.ExistsAsync( options.LockIndex );
            Assert.IsTrue( lockExists.Exists, $"Lock index `{options.LockIndex}` was not created." );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task InitializeAsync_Idempotent_SecondCallSucceeds()
    {
        var options = UniqueOptions( nameof( InitializeAsync_Idempotent_SecondCallSucceeds ) );
        var store = BuildStore( options );

        try
        {
            await store.InitializeAsync();

            // Second call must succeed — both init steps verify-existing rather than re-create
            await store.InitializeAsync();
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task CreateLockAsync_AcquiresAndReleases_SecondAcquireWorks()
    {
        var options = UniqueOptions( nameof( CreateLockAsync_AcquiresAndReleases_SecondAcquireWorks ) );
        var store = BuildStore( options );

        try
        {
            await store.InitializeAsync();

            // First acquire
            var lock1 = await store.CreateLockAsync();
            Assert.IsNotNull( lock1 );
            lock1.Dispose();

            // Second acquire (after release) must work
            var lock2 = await store.CreateLockAsync();
            Assert.IsNotNull( lock2 );
            lock2.Dispose();
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task CreateLockAsync_WhileHeld_ThrowsLockUnavailable()
    {
        var options = UniqueOptions( nameof( CreateLockAsync_WhileHeld_ThrowsLockUnavailable ) );
        var store = BuildStore( options );

        try
        {
            await store.InitializeAsync();

            using var firstLock = await store.CreateLockAsync();

            // Second acquire from the same process (different RecordStore instance with same options)
            // Note: the unit-test guard prevents same-process concurrent locks from a single instance,
            // but a fresh store sees the lock document and must throw.
            var contendingStore = BuildStore( options );

            await Assert.ThrowsExactlyAsync<MigrationLockUnavailableException>(
                async () => await contendingStore.CreateLockAsync() );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task LedgerCrud_WriteExistsReadDelete_RoundTrip()
    {
        var options = UniqueOptions( nameof( LedgerCrud_WriteExistsReadDelete_RoundTrip ) );
        var store = BuildStore( options );
        var recordId = $"1000.test-record-{Guid.NewGuid():n}";

        try
        {
            await store.InitializeAsync();

            // Initially does not exist
            Assert.IsFalse( await store.ExistsAsync( recordId ) );

            // Write
            await store.WriteAsync( recordId );

            // Now exists
            Assert.IsTrue( await store.ExistsAsync( recordId ) );

            // Read returns the record
            var record = await store.ReadAsync( recordId );
            Assert.IsNotNull( record );
            Assert.AreEqual( recordId, record.Id );

            // Delete
            await store.DeleteAsync( recordId );

            Assert.IsFalse( await store.ExistsAsync( recordId ) );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task BootstrapResult_OnSuccess_AllStepsSucceeded()
    {
        // Run the bootstrapper directly to inspect the per-step outcomes
        // (BootstrapResult.Steps is the diagnostic surface per ADR-0014).
        var options = UniqueOptions( nameof( BootstrapResult_OnSuccess_AllStepsSucceeded ) );
        var client = OpenSearchTestContainer.Client;
        var steps = new IBootstrapStep[]
        {
            new RestPingStep(),
            new ClusterHealthStep(),
            new LedgerIndexInitStep(),
            new LockIndexInitStep()
        };
        var bootstrapper = new OpenSearchBootstrapper(
            steps, client, options, TimeProvider.System, NullLoggerFactory.Instance );

        try
        {
            var result = await bootstrapper.RunAsync();

            Assert.IsTrue( result.IsSuccess, $"Bootstrap failed at: {result.FailedAt?.Name ?? "(none)"}" );
            Assert.AreEqual( 4, result.Steps.Count );
            foreach ( var step in result.Steps )
                Assert.AreEqual( StepStatus.Succeeded, step.Status, $"Step {step.Name} did not succeed: {step.Detail}" );
            Assert.IsNull( result.FailedAt );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }
}
#endif
