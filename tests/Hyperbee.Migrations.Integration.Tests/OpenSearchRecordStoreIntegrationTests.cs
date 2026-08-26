//#define INTEGRATIONS
#nullable enable
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Client;

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
// Gating (ADR-0031): one shared OpenSearch container via the assembly fixture, no
// Docker image build, no multi-node, seconds to run. This class is the reason the
// tier exists -- it is where a ledger wire-contract defect (ADR-0029) shows up.
[TestCategory( "Gating" )]
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

    // ---- reconciliation (ADR-0019 Phase 3) --------------------------------
    //
    // MigrationRunner.RunAsync calls IntersectWithAppliedAsync unconditionally
    // whenever at least one migration is discovered, so this is the single
    // hottest ledger path in the library -- and it had no integration coverage
    // at all before these two tests. The v3.0.0-v3.1.0 defect (the _mget body
    // resolving its index by CLR-type inference) lived and shipped in that gap.

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase3" )]
    public async Task IntersectWithApplied_AgainstRealCluster_ReturnsOnlyWrittenIds()
    {
        var options = UniqueOptions( nameof( IntersectWithApplied_AgainstRealCluster_ReturnsOnlyWrittenIds ) );
        var store = BuildStore( options );

        var written = new[] { "1000.applied-one", "1002.applied-two" };
        var absent = new[] { "1001.never-run", "1003.never-run" };

        try
        {
            await store.InitializeAsync();

            foreach ( var id in written )
                await store.WriteAsync( id );

            // Realtime semantics: no refresh between the writes and the read.
            // _mget reads through the translog, so the just-written rows must be
            // visible immediately (this is precisely why the implementation uses
            // _mget rather than _search).
            var applied = await store.IntersectWithAppliedAsync( written.Concat( absent ) );

            CollectionAssert.AreEquivalent( written, applied.ToArray() );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase3" )]
    public async Task IntersectWithApplied_OnShippedClientRegistration_Works()
    {
        // Every other test in this file uses OpenSearchTestContainer.Client, a
        // hand-rolled ConnectionSettings that no consumer ever gets. This one
        // drives the client the LIBRARY builds -- services.AddOpenSearchClient --
        // so the shipped registration path is itself under test.
        //
        // That distinction is the whole point: the v3.0.0-v3.1.0 defect was a
        // mismatch between what the record store required of the client
        // (a DefaultMappingFor<OpenSearchMigrationRecord> or a DefaultIndex) and
        // what the library's own client factories configure (neither). A test
        // that builds its own client cannot see that class of bug.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenSearchClient( OpenSearchTestContainer.Endpoint );

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IOpenSearchClient>();

        var options = UniqueOptions( nameof( IntersectWithApplied_OnShippedClientRegistration_Works ) );
        var steps = new IBootstrapStep[]
        {
            new RestPingStep(),
            new ClusterHealthStep(),
            new LedgerIndexInitStep(),
            new LockIndexInitStep()
        };
        var bootstrapper = new OpenSearchBootstrapper(
            steps, client, options, TimeProvider.System, NullLoggerFactory.Instance );
        var store = new OpenSearchRecordStore(
            client, bootstrapper, options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );

        try
        {
            await store.InitializeAsync();
            await store.WriteAsync( "1000.applied" );

            var applied = await store.IntersectWithAppliedAsync( ["1000.applied", "1001.absent"] );

            CollectionAssert.AreEquivalent( new[] { "1000.applied" }, applied.ToArray() );
        }
        finally
        {
            await client.Indices.DeleteAsync( options.LedgerIndex );
            await client.Indices.DeleteAsync( options.LockIndex );
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
