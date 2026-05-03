//#define INTEGRATIONS
#nullable enable
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Phase 2 Slice 2.5 — R-19 partial-rollback semantics integration tests
// against a real OpenSearch cluster. These cover the load-bearing
// production-correctness contract:
//
//   1. Down direction with all rollbacks supported -> full rollback succeeds
//   2. Down halts when rollback statement N fails (R-24c (n) keystone) ->
//      ledger updated to status=partially_rolled_back with failedStatementIndex=N
//   3. Subsequent ExistsAsync on a partially-rolled-back record THROWS
//      OpenSearchPartialRollbackException unless ForceResume is set
//   4. ForceResume=true bypasses the lockout
//
// Each test gets a unique slug so concurrent runs don't collide.

[TestClass]
public class OpenSearchPartialRollbackIntegrationTests
{
    [Migration( 9101L )]
    private sealed class FakeMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    private string _slug = null!;
    private OpenSearchMigrationOptions _options = null!;
    private OpenSearchRecordStore _recordStore = null!;
    private OpenSearchResourceRunner<FakeMigration> _runner = null!;
    private string _alphaIndex = null!;
    private string _bravoIndex = null!;
    private string _charlieIndex = null!;
    private string _recordId = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _slug = Guid.NewGuid().ToString( "n" );
        _alphaIndex = $"alpha-{_slug}";
        _bravoIndex = $"bravo-{_slug}";
        _charlieIndex = $"charlie-{_slug}";
        _recordId = $"rec-{_slug}";

        _options = new OpenSearchMigrationOptions
        {
            LedgerIndex = $".migrations-rb-{_slug}",
            LockIndex = $".migrations-rb-lock-{_slug}",
            LockName = $"lock-rb-{_slug}",
            LockRenewInterval = TimeSpan.FromSeconds( 10 ),
            LockStaleAfter = TimeSpan.FromSeconds( 30 ),
            LockMaxLifetime = TimeSpan.FromMinutes( 5 ),
            WaitMode = WaitMode.Off
        };

        var client = OpenSearchTestContainer.Client;
        var bootstrapper = new OpenSearchBootstrapper(
            new IBootstrapStep[]
            {
                new RestPingStep(),
                new ClusterHealthStep(),
                new LedgerIndexInitStep(),
                new LockIndexInitStep()
            },
            client, _options, TimeProvider.System, NullLoggerFactory.Instance );

        _recordStore = new OpenSearchRecordStore(
            client, bootstrapper, _options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );

        await _recordStore.InitializeAsync();

        var dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        var parser = new OpenSearchStatementParser();
        _runner = new OpenSearchResourceRunner<FakeMigration>(
            client, _options, dispatcher, parser, TimeProvider.System,
            NullLogger<FakeMigration>.Instance, _recordStore );

        // Pre-create three indices that the rollback statements will drop.
        // The Up migration is simulated; we only test the Down path here.
        var ll = OpenSearchTestContainer.LowLevelClient;
        await ll.Indices.CreateAsync<StringResponse>( _alphaIndex, PostData.String( "{}" ) );
        await ll.Indices.CreateAsync<StringResponse>( _bravoIndex, PostData.String( "{}" ) );
        await ll.Indices.CreateAsync<StringResponse>( _charlieIndex, PostData.String( "{}" ) );

        // Seed an Up record so partial-rollback writes overwrite an existing
        // entry (the realistic case: a previous run wrote status=succeeded).
        await _recordStore.WriteAsync( _recordId );
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        await ll.Indices.DeleteAsync<StringResponse>( $"{_alphaIndex},{_bravoIndex},{_charlieIndex}" );
        await ll.Indices.DeleteAsync<StringResponse>( _options.LedgerIndex );
        await ll.Indices.DeleteAsync<StringResponse>( _options.LockIndex );
    }

    private static async Task<bool> IndexExistsAsync( string indexName )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        var resp = await ll.Indices.ExistsAsync<StringResponse>( indexName );
        return resp.HttpStatusCode == 200;
    }

    // ---- happy path: full rollback succeeds, all three indices dropped ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-19" )]
    public async Task Rollback_AllStatementsSupported_ExecutesInReverse()
    {
        var json = $$"""
            {
              "statements": [
                { "statement": "CREATE INDEX {{_alphaIndex}}",   "rollback": "DROP INDEX {{_alphaIndex}} IF EXISTS" },
                { "statement": "CREATE INDEX {{_bravoIndex}}",   "rollback": "DROP INDEX {{_bravoIndex}} IF EXISTS" },
                { "statement": "CREATE INDEX {{_charlieIndex}}", "rollback": "DROP INDEX {{_charlieIndex}} IF EXISTS" }
              ]
            }
            """;

        await _runner.RollbackStatementsFromJsonAsync( json, _recordId );

        // All three indices should be dropped — IF EXISTS guards make the
        // operation idempotent so re-rolling is safe too.
        Assert.IsFalse( await IndexExistsAsync( _alphaIndex ) );
        Assert.IsFalse( await IndexExistsAsync( _bravoIndex ) );
        Assert.IsFalse( await IndexExistsAsync( _charlieIndex ) );
    }

    // ---- R-24c (n) keystone: partial rollback writes ledger correctly ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-19" )]
    [TestCategory( "R-24c" )]
    public async Task Rollback_FailsAtMiddleStatement_LedgerMarkedPartiallyRolledBack()
    {
        // R-19 / R-24c (n): rollback statement N fails after N+1..M succeeded.
        // Rollbacks dispatch in reverse: index 2 (charlie drop) succeeds
        // first, then index 1 fails, then index 0 (alpha) is never reached.
        //
        // Induced failure on the middle rollback: CREATE INDEX over an
        // already-existing index name without IF NOT EXISTS. The cluster
        // returns 400 + resource_already_exists_exception, which BuildResult
        // reliably maps to Failed.

        var json = $$"""
            {
              "statements": [
                { "statement": "CREATE INDEX {{_alphaIndex}}",   "rollback": "DROP INDEX {{_alphaIndex}} IF EXISTS" },
                { "statement": "CREATE INDEX {{_bravoIndex}}",   "rollback": "CREATE INDEX {{_bravoIndex}}" },
                { "statement": "CREATE INDEX {{_charlieIndex}}", "rollback": "DROP INDEX {{_charlieIndex}} IF EXISTS" }
              ]
            }
            """;

        // Should throw MigrationException after partial rollback.
        try
        {
            await _runner.RollbackStatementsFromJsonAsync( json, _recordId );
            Assert.Fail( "expected MigrationException from failing rollback at index 1" );
        }
        catch ( MigrationException ex )
        {
            StringAssert.Contains( ex.Message, "index 1" );
        }

        // Charlie was dropped (index 2 rolled back first).
        Assert.IsFalse( await IndexExistsAsync( _charlieIndex ),
            "charlie should have been dropped before the failing bravo rollback" );

        // Alpha was NOT dropped (index 0 never reached).
        Assert.IsTrue( await IndexExistsAsync( _alphaIndex ),
            "alpha should still exist — its rollback was not reached" );

        // Bravo's rollback failed; bravo still exists.
        Assert.IsTrue( await IndexExistsAsync( _bravoIndex ),
            "bravo should still exist — its rollback failed" );

        // Ledger was overwritten with status=partially_rolled_back +
        // failedStatementIndex=1.
        var raw = await ReadRawRecordAsync( _recordId );
        StringAssert.Contains( raw, "partially_rolled_back" );
        StringAssert.Contains( raw, "\"failedStatementIndex\":1" );
    }

    // ---- subsequent runs are blocked unless ForceResume ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-19" )]
    public async Task ExistsAsync_OnPartiallyRolledBackRecord_Throws()
    {
        // Drive the record to partially_rolled_back state directly.
        await _recordStore.WritePartialRollbackAsync( _recordId, failedStatementIndex: 2, error: "test" );

        // ForceResume default = false; ExistsAsync throws.
        try
        {
            await _recordStore.ExistsAsync( _recordId );
            Assert.Fail( "expected OpenSearchPartialRollbackException" );
        }
        catch ( OpenSearchPartialRollbackException ex )
        {
            Assert.AreEqual( _recordId, ex.RecordId );
            Assert.AreEqual( 2, ex.FailedStatementIndex );
            StringAssert.Contains( ex.Message, "partially_rolled_back" );
            StringAssert.Contains( ex.Message, "ForceResume" );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-19" )]
    public async Task ExistsAsync_OnPartiallyRolledBackRecord_WithForceResume_ReturnsTrue()
    {
        await _recordStore.WritePartialRollbackAsync( _recordId, failedStatementIndex: 1, error: "test" );

        // Operator has reconciled state and opts in.
        _options.ForceResume = true;

        var exists = await _recordStore.ExistsAsync( _recordId );
        Assert.IsTrue( exists, "ForceResume should bypass the lockout and return true" );
    }

    // ---- ledger schema verification: forensic fields populated on Up writes ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-19" )]
    public async Task WriteAsync_PopulatesForensicFields_DirectionStatusAppliedBy()
    {
        // The standard Up write contract (IMigrationRecordStore.WriteAsync)
        // is a string recordId. The OpenSearch implementation populates the
        // R-06 forensic fields (direction=Up, status=succeeded, appliedBy)
        // automatically.
        var fresh = $"fresh-{_slug}";
        await _recordStore.WriteAsync( fresh );

        var raw = await ReadRawRecordAsync( fresh );
        StringAssert.Contains( raw, "\"direction\":\"Up\"" );
        StringAssert.Contains( raw, "\"status\":\"succeeded\"" );
        StringAssert.Contains( raw, $"\"appliedBy\":\"{Environment.MachineName}/{Environment.ProcessId}\"" );
    }

    // ---- helpers ----

    private async Task<string> ReadRawRecordAsync( string recordId )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        var resp = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.GET, $"{_options.LedgerIndex}/_doc/{recordId}", default );
        Assert.AreEqual( 200, resp.HttpStatusCode );
        return resp.Body;
    }
}
#endif
