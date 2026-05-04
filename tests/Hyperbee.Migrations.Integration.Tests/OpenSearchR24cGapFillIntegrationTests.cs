//#define INTEGRATIONS
#nullable enable
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// R-24c production-scenario gap-fill. The canonical R-24c table lists 15
// scenarios; (a)/(b)/(h)/(j)/(n)/(o) are covered by earlier slices, (e)
// needs Tasks API (deferred to plan task 2.1), (f) needs toxiproxy
// infrastructure (deferred), (l) was REMOVED per ADR-0016. This file
// covers the remaining single-node-runnable gaps:
//
//   (c) Mapping update on existing index produces "no reindex" gotcha
//       diagnostic
//   (d) Static settings update fails clearly without CLOSE, succeeds with it
//   (g) dynamic:strict rejects unexpected fields
//   (i) Reindex op_type:create skips partial-prior-run docs (no double-write)
//
// Multi-node-only scenarios (k, m) live in OpenSearchR24cMultiNodeTests.

[TestClass]
public class OpenSearchR24cGapFillIntegrationTests
{
    private OpenSearchStatementParser _parser = null!;
    private StatementDispatcher _dispatcher = null!;
    private OpenSearchMigrationOptions _options = null!;
    private string _slug = null!;

    [TestInitialize]
    public void Setup()
    {
        _parser = new OpenSearchStatementParser();
        _dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        _options = new OpenSearchMigrationOptions { WaitMode = WaitMode.Off };
        _slug = Guid.NewGuid().ToString( "n" );
    }

    private async Task<StatementResult> DispatchAsync( string statement, ILogger? logger = null, JsonNode? body = null )
    {
        var ast = _parser.Parse( statement );
        var ctx = new StatementContext
        {
            Client = OpenSearchTestContainer.Client,
            Options = _options,
            TimeProvider = TimeProvider.System,
            Logger = logger ?? NullLogger.Instance,
            ResolvedBody = body,
            CancellationToken = default
        };
        return await _dispatcher.DispatchAsync( ast, ctx );
    }

    // ---- R-24c (c) — UPDATE MAPPING "no reindex" gotcha diagnostic ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24c" )]
    public async Task UpdateMapping_OnExistingIndex_LogsNoReindexGotchaDiagnostic()
    {
        // R-24c (c): the diagnostic must surface the "mapping update doesn't
        // reindex existing data" gotcha so authors don't silently get
        // wrong-state behavior. Pinning the log presence here so a
        // refactor that removes the diagnostic is caught immediately.
        var index = $"r24c-c-{_slug}";
        var ll = OpenSearchTestContainer.LowLevelClient;

        await ll.Indices.CreateAsync<StringResponse>( index, PostData.String( """
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
              "mappings": { "properties": { "id": { "type": "keyword" } } }
            }
            """ ) );

        try
        {
            var capture = new CapturingLogger();
            var mappingBody = JsonNode.Parse( """
                { "properties": { "name": { "type": "text" } } }
                """ );

            var result = await DispatchAsync(
                $"UPDATE MAPPING ON {index} WITH BODY $body", logger: capture, body: mappingBody );

            Assert.IsTrue( result.IsSuccess, $"mapping update should succeed; got: {result.Detail}" );

            Assert.IsTrue(
                capture.Messages.Any( m => m.Contains( "do NOT reindex" ) || m.Contains( "MIGRATE INDEX" ) ),
                "dispatcher must emit a diagnostic naming the no-reindex gotcha and pointing at MIGRATE INDEX. " +
                $"Captured messages: {string.Join( " | ", capture.Messages )}" );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( index );
        }
    }

    // ---- R-24c (d) — Static settings update needs CLOSE ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24c" )]
    public async Task UpdateSettings_StaticSettingsWithoutClose_Fails()
    {
        // R-24c (d): static settings (e.g., number_of_shards, codec, analysis)
        // can't be changed on an open index. UPDATE SETTINGS without CLOSE
        // sends a direct PUT _settings that the cluster rejects clearly.
        var index = $"r24c-d1-{_slug}";
        var ll = OpenSearchTestContainer.LowLevelClient;

        await ll.Indices.CreateAsync<StringResponse>( index, PostData.String( """
            {
              "settings": {
                "number_of_shards": 1,
                "number_of_replicas": 0,
                "analysis": {
                  "analyzer": {
                    "default": { "type": "standard" }
                  }
                }
              }
            }
            """ ) );

        try
        {
            // Try to swap the default analyzer — that's static, requires CLOSE.
            var settingsBody = JsonNode.Parse( """
                {
                  "analysis": {
                    "analyzer": {
                      "default": { "type": "whitespace" }
                    }
                  }
                }
                """ );

            var result = await DispatchAsync(
                $"UPDATE SETTINGS ON {index} WITH BODY $body", body: settingsBody );

            Assert.IsFalse( result.IsSuccess,
                $"static-settings update without CLOSE must fail; got: {result.Detail}" );
            StringAssert.Contains( result.Detail!, "analysis",
                "the cluster's rejection message should mention the offending setting" );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( index );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24c" )]
    public async Task UpdateSettings_StaticSettingsWithClose_Succeeds()
    {
        // Same shape with CLOSE — dispatcher does close → update → open
        // dance. Authors explicitly acknowledge the brief write-
        // unavailability window via the keyword.
        var index = $"r24c-d2-{_slug}";
        var ll = OpenSearchTestContainer.LowLevelClient;

        await ll.Indices.CreateAsync<StringResponse>( index, PostData.String( """
            {
              "settings": {
                "number_of_shards": 1,
                "number_of_replicas": 0,
                "analysis": {
                  "analyzer": { "default": { "type": "standard" } }
                }
              }
            }
            """ ) );

        try
        {
            var settingsBody = JsonNode.Parse( """
                {
                  "analysis": {
                    "analyzer": { "default": { "type": "whitespace" } }
                  }
                }
                """ );

            var result = await DispatchAsync(
                $"UPDATE SETTINGS ON {index} CLOSE WITH BODY $body", body: settingsBody );

            Assert.IsTrue( result.IsSuccess,
                $"static-settings update WITH CLOSE must succeed; got: {result.Detail}" );

            // Verify the index is OPEN again (the dispatcher's close-update-
            // open dance must always reopen, even on settings failure).
            var statsResp = await ll.DoRequestAsync<StringResponse>(
                global::OpenSearch.Net.HttpMethod.GET, $"_cat/indices/{index}", default );
            Assert.IsTrue( statsResp.Success );
            StringAssert.Contains( statsResp.Body!, "open",
                "index must be reopened after the CLOSE-update-OPEN dance" );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( index );
        }
    }

    // ---- R-24c (g) — dynamic:strict rejects unmapped fields ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24c" )]
    [TestCategory( "R-17" )]
    public async Task DynamicStrict_AutoInjected_RejectsUnmappedFields()
    {
        // R-17 + R-24c (g): the provider's auto-inject of dynamic:strict on
        // CREATE INDEX bodies that don't already pin dynamic must result in
        // the cluster rejecting writes that include unmapped fields. This is
        // the load-bearing safety: silent acceptance of unmapped fields
        // creates mapping explosion and silent type mismatches.
        var index = $"r24c-g-{_slug}";
        var idxBody = JsonNode.Parse( """
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
              "mappings": { "properties": { "id": { "type": "keyword" } } }
            }
            """ );

        var createResult = await DispatchAsync(
            $"CREATE INDEX {index} WITH BODY $body", body: idxBody );
        Assert.IsTrue( createResult.IsSuccess );

        try
        {
            var ll = OpenSearchTestContainer.LowLevelClient;

            // Indexing with only the mapped field should succeed.
            var ok = await ll.IndexAsync<StringResponse>(
                index, "1", PostData.String( """{ "id": "u1" }""" ) );
            Assert.IsTrue( ok.Success, $"mapped-only doc should index; got: {ok.Body}" );

            // Indexing with an UNMAPPED field must be rejected by
            // strict_dynamic_mapping.
            var rejected = await ll.IndexAsync<StringResponse>(
                index, "2", PostData.String( """{ "id": "u2", "unmapped_field": "x" }""" ) );
            Assert.IsFalse( rejected.Success,
                $"unmapped field should be rejected by dynamic:strict; got HTTP {rejected.HttpStatusCode}: {rejected.Body}" );
            StringAssert.Contains( rejected.Body!, "strict_dynamic_mapping" );
        }
        finally
        {
            await OpenSearchTestContainer.LowLevelClient.Indices.DeleteAsync<StringResponse>( index );
        }
    }

    // ---- R-24c (i) — Reindex op_type:create skips prior-run partial docs ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-24c" )]
    [TestCategory( "R-08a" )]
    public async Task Reindex_PartialPriorRunDocs_OpTypeCreateSkipsThemSafely()
    {
        // R-24c (i) / PM-3: a crashed prior run leaves dst with partial docs.
        // The new run with op_type:create (auto-injected by R-08a) must skip
        // those without overwriting them, and reindex the remainder.
        // op_type:create returns 409 for already-existing IDs — which the
        // bulk reindex tolerates as "version_conflict_engine_exception"
        // failures-but-not-errors per default reindex semantics.

        var src = $"r24c-i-src-{_slug}";
        var dst = $"r24c-i-dst-{_slug}";
        var ll = OpenSearchTestContainer.LowLevelClient;

        // Permissive index for seeding (bypass dispatcher's dynamic:strict)
        var permissiveBody = """
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
              "mappings": { "properties": { "id": { "type": "keyword" }, "v": { "type": "long" } } }
            }
            """;
        await ll.Indices.CreateAsync<StringResponse>( src, PostData.String( permissiveBody ) );
        await ll.Indices.CreateAsync<StringResponse>( dst, PostData.String( permissiveBody ) );

        try
        {
            // Seed src with 5 docs at v=1.
            for ( var i = 0; i < 5; i++ )
                await ll.IndexAsync<StringResponse>( src, $"u{i}", PostData.String( $"{{\"id\":\"u{i}\",\"v\":1}}" ) );
            await ll.Indices.RefreshAsync<StringResponse>( src );

            // Simulate a crashed prior run: dst already has u0 + u1 written
            // at v=99 (the value that would be lost if op_type:create were
            // disabled and the reindex overwrote them).
            await ll.IndexAsync<StringResponse>( dst, "u0", PostData.String( """{"id":"u0","v":99}""" ) );
            await ll.IndexAsync<StringResponse>( dst, "u1", PostData.String( """{"id":"u1","v":99}""" ) );
            await ll.Indices.RefreshAsync<StringResponse>( dst );

            // Run REINDEX — op_type:create is auto-injected.
            var result = await DispatchAsync( $"REINDEX FROM {src} TO {dst}" );
            // The reindex itself reports created vs failed counts in the
            // response body; the cluster does NOT mark the operation failed
            // overall. Our dispatcher's BuildResult treats HTTP 200 as
            // Executed regardless of internal version-conflict failures,
            // because the overall reindex semantics-with-op_type:create
            // explicitly include "skip-on-conflict" as a documented behavior.
            Assert.IsTrue( result.IsSuccess, $"reindex should succeed: {result.Detail}" );

            await ll.Indices.RefreshAsync<StringResponse>( dst );

            // Critical: u0 and u1 in dst still have v=99 (NOT overwritten).
            var u0 = await ll.GetAsync<StringResponse>( dst, "u0" );
            using var u0Doc = JsonDocument.Parse( u0.Body );
            var u0v = u0Doc.RootElement.GetProperty( "_source" ).GetProperty( "v" ).GetInt64();
            Assert.AreEqual( 99, u0v,
                "u0 in dst was pre-existing (simulating a crashed prior run); " +
                "op_type:create must NOT have overwritten its v=99 with the src's v=1" );

            // u2..u4 should now exist in dst at v=1 (the "remainder").
            var u4 = await ll.GetAsync<StringResponse>( dst, "u4" );
            Assert.IsTrue( u4.Success && u4.Body.Contains( "\"v\":1" ),
                $"new docs must be reindexed: {u4.Body}" );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( $"{src},{dst}" );
        }
    }

    // ---- captured-log helper ----

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>( TState state ) where TState : notnull => NoopDisposable.Instance;
        public bool IsEnabled( LogLevel logLevel ) => true;
        public void Log<TState>( LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter )
        {
            Messages.Add( formatter( state, exception ) );
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}

// ---- Multi-node R-24c scenarios (k, m) ----

[TestClass]
public class OpenSearchR24cMultiNodeIntegrationTests
{
    [ClassInitialize]
    public static async Task ClassSetup( TestContext context )
    {
        await MultiNodeOpenSearchTestContainer.InitializeAsync( context.CancellationTokenSource.Token );
    }

    [ClassCleanup]
    public static async Task ClassTeardown()
    {
        await MultiNodeOpenSearchTestContainer.DisposeAsync();
    }

    private string _slug = null!;

    [TestInitialize]
    public void Setup()
    {
        _slug = Guid.NewGuid().ToString( "n" );
    }

    // ---- R-24c (k) — Concurrent lock acquire on multi-node ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "MultiNode" )]
    [TestCategory( "R-24c" )]
    [TestCategory( "PA-2" )]
    public async Task ConcurrentLockAcquire_OnMultiNode_OnlyOneWinner_BoundedTailLatency()
    {
        // R-24c (k) / PA-2: N concurrent CreateLockAsync calls. Exactly one
        // winner; losers fail fast (not held for replica-write coupling
        // delays). The lock-index `number_of_replicas: 0` setting ensures
        // the contention is on the primary only — Slice 2.11 verified the
        // setting; this test verifies the runtime BEHAVIOR under contention.
        var options = new OpenSearchMigrationOptions
        {
            LedgerIndex = $".migrations-mn-k-{_slug}",
            LockIndex = $".migrations-mn-k-lock-{_slug}",
            LockName = $"lock-mn-k-{_slug}",
            LockRenewInterval = TimeSpan.FromSeconds( 10 ),
            LockStaleAfter = TimeSpan.FromSeconds( 30 ),
            LockMaxLifetime = TimeSpan.FromMinutes( 5 )
        };

        var client = MultiNodeOpenSearchTestContainer.Client;
        var bootstrapper = new OpenSearchBootstrapper(
            new IBootstrapStep[]
            {
                new RestPingStep(),
                new ClusterHealthStep(),
                new LedgerIndexInitStep(),
                new LockIndexInitStep()
            },
            client, options, TimeProvider.System, NullLoggerFactory.Instance );

        var store = new OpenSearchRecordStore(
            client, bootstrapper, options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );

        await store.InitializeAsync();
        try
        {
            const int n = 8;
            var stopwatches = new Stopwatch[n];
            var tasks = Enumerable.Range( 0, n )
                .Select( i => Task.Run( async () =>
                {
                    stopwatches[i] = Stopwatch.StartNew();
                    try
                    {
                        return await store.CreateLockAsync();
                    }
                    catch ( MigrationLockUnavailableException )
                    {
                        return null;
                    }
                    finally
                    {
                        stopwatches[i].Stop();
                    }
                } ) )
                .ToArray();

            var results = await Task.WhenAll( tasks );
            var winners = results.Count( r => r is not null );
            Assert.AreEqual( 1, winners,
                $"exactly one of {n} concurrent acquires must win; got {winners}" );

            // Tail latency: with replicas:0, losers should fail within the
            // op_type=create round trip — well under 5s on a healthy 3-node
            // cluster. If replicas were 1+, replica-write coupling could
            // stretch losers out and break this assertion.
            var maxMs = stopwatches.Max( s => s.ElapsedMilliseconds );
            Assert.IsTrue( maxMs < 5000,
                $"tail latency under contention bounded; max observed {maxMs}ms across {n} attempts " +
                "(expected <5s with replicas:0; PA-2)" );

            // Cleanup: dispose the winning lock.
            foreach ( var r in results )
                r?.Dispose();
        }
        finally
        {
            var ll = MultiNodeOpenSearchTestContainer.LowLevelClient;
            await ll.Indices.DeleteAsync<StringResponse>( options.LedgerIndex );
            await ll.Indices.DeleteAsync<StringResponse>( options.LockIndex );
        }
    }

    // ---- R-24c (m) — Ledger refresh budget at scale ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "MultiNode" )]
    [TestCategory( "R-24c" )]
    [TestCategory( "R-07" )]
    public async Task LedgerWrite_HundredMigrations_CompletesWithinBudget()
    {
        // R-24c (m) / PA-1 / R-07: ledger writes use refresh=wait_for so
        // ExistsAsync after Write is reliable. The budget concern is that
        // 100 sequential writes with wait_for don't accumulate
        // pathologically due to per-write refresh stalls. Budget: 60s on a
        // 3-node cluster with replicas:0 on the ledger. Generous enough to
        // tolerate reasonable variance; tight enough that a refresh-storm
        // regression breaks the test.
        const int migrationCount = 100;
        const int budgetSeconds = 60;

        var options = new OpenSearchMigrationOptions
        {
            LedgerIndex = $".migrations-mn-m-{_slug}",
            LockIndex = $".migrations-mn-m-lock-{_slug}",
            LockName = $"lock-mn-m-{_slug}",
            LockRenewInterval = TimeSpan.FromSeconds( 10 ),
            LockStaleAfter = TimeSpan.FromSeconds( 30 ),
            LockMaxLifetime = TimeSpan.FromMinutes( 5 )
        };

        var client = MultiNodeOpenSearchTestContainer.Client;
        var bootstrapper = new OpenSearchBootstrapper(
            new IBootstrapStep[]
            {
                new RestPingStep(),
                new ClusterHealthStep(),
                new LedgerIndexInitStep(),
                new LockIndexInitStep()
            },
            client, options, TimeProvider.System, NullLoggerFactory.Instance );

        var store = new OpenSearchRecordStore(
            client, bootstrapper, options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );

        await store.InitializeAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            for ( var i = 0; i < migrationCount; i++ )
                await store.WriteAsync( $"m-{_slug}-{i:D3}" );
            sw.Stop();

            Assert.IsTrue( sw.Elapsed.TotalSeconds < budgetSeconds,
                $"ledger budget exceeded: {migrationCount} writes took {sw.Elapsed.TotalSeconds:F1}s (budget {budgetSeconds}s). " +
                "Investigate refresh-wait pile-up or shard allocation issues." );

            // Sanity: all writes are queryable post-write (the wait_for
            // semantics that R-07 promises).
            var existsCount = 0;
            for ( var i = 0; i < migrationCount; i++ )
                if ( await store.ExistsAsync( $"m-{_slug}-{i:D3}" ) )
                    existsCount++;
            Assert.AreEqual( migrationCount, existsCount,
                $"all {migrationCount} writes should be visible via ExistsAsync after wait_for" );
        }
        finally
        {
            var ll = MultiNodeOpenSearchTestContainer.LowLevelClient;
            await ll.Indices.DeleteAsync<StringResponse>( options.LedgerIndex );
            await ll.Indices.DeleteAsync<StringResponse>( options.LockIndex );
        }
    }
}
#endif
