//#define INTEGRATIONS
#nullable enable
using System.Text.Json;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Phase 2 Slice 2.4 — WHEN VERSION (R-15a) integration tests against real
// OpenSearch. Validates the live cluster-version probe (GET /) and the
// predicate-skip semantics. Cluster reports MAJOR.MINOR.PATCH; the
// Testcontainers image is pinned to 2.18.0 so we have a deterministic
// version to write predicates against.

[TestClass]
// Gating (ADR-0031): shared assembly-fixture container, no Docker image build,
// not multi-node. Runs on every PR.
[TestCategory( "Gating" )]
public class OpenSearchWhenVersionIntegrationTests
{
    private OpenSearchStatementParser _parser = null!;
    private StatementDispatcher _dispatcher = null!;
    private OpenSearchMigrationOptions _options = null!;
    private string _slug = null!;
    private string _indexName = null!;

    [TestInitialize]
    public void Setup()
    {
        _parser = new OpenSearchStatementParser();
        _dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        _options = new OpenSearchMigrationOptions { WaitMode = WaitMode.Off };

        _slug = Guid.NewGuid().ToString( "n" );
        _indexName = $"wv-{_slug}";
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        await ll.Indices.DeleteAsync<StringResponse>( _indexName );
    }

    private Task<StatementResult> DispatchAsync( string statement )
    {
        var ast = _parser.Parse( statement );
        var ctx = new StatementContext
        {
            Client = OpenSearchTestContainer.Client,
            Options = _options,
            TimeProvider = TimeProvider.System,
            Logger = NullLogger.Instance,
            ResolvedBody = null,
            CancellationToken = default
        };
        return _dispatcher.DispatchAsync( ast, ctx );
    }

    private static async Task<bool> IndexExistsAsync( string index )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        var resp = await ll.Indices.ExistsAsync<StringResponse>( index );
        return resp.HttpStatusCode == 200;
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task WhenVersion_PredicateTrue_DispatchesChild()
    {
        // Cluster is 2.18.0 (Testcontainers pin); `>= '2.0'` is trivially true.
        var result = await DispatchAsync( $"WHEN VERSION >= '2.0' CREATE INDEX {_indexName}" );

        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );
        Assert.AreEqual( StatementOutcome.Executed, result.Outcome );
        Assert.IsTrue( await IndexExistsAsync( _indexName ),
            "child statement should have created the index" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task WhenVersion_PredicateFalse_SkipsChild()
    {
        // `>= '99.0'` is unreachable; child should NOT dispatch.
        var result = await DispatchAsync( $"WHEN VERSION >= '99.0' CREATE INDEX {_indexName}" );

        Assert.AreEqual( StatementOutcome.Skipped, result.Outcome );
        Assert.IsFalse( await IndexExistsAsync( _indexName ),
            "skipped child must not have created the index" );
        StringAssert.Contains( result.Detail!, "does not satisfy" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-15a" )]
    public async Task WhenVersion_2_9_LessThan_2_10_LiveCluster_DispatchesAsExpected()
    {
        // R-15a load-bearing case proven against the live cluster: predicate
        // `<= '2.9'` evaluates against a 2.18 cluster and should be false.
        // (If lex sort was being used, `'2.18' <= '2.9'` would be true and the
        // child would dispatch — wrong-state on every prod cluster running 2.10+.)
        var result = await DispatchAsync( $"WHEN VERSION <= '2.9' CREATE INDEX {_indexName}" );

        Assert.AreEqual( StatementOutcome.Skipped, result.Outcome,
            "cluster (2.18+) is NOT <= 2.9; under semver, predicate is false. " +
            "If this assertion fails, check the comparator — lexical sort would invert it." );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task WhenVersion_FetchesClusterVersionOnce_PerDispatcher()
    {
        // Lifecycle assertion: the cluster version is cached after the first
        // probe. We can't assert request counts without instrumentation, but
        // we can assert behavioral consistency — three sequential
        // dispatches with different predicates against the same dispatcher
        // instance all succeed without re-probing failures.
        var r1 = await DispatchAsync( $"WHEN VERSION >= '2.0' CREATE INDEX {_indexName}" );
        var r2 = await DispatchAsync( $"WHEN VERSION >= '2.0' DROP INDEX {_indexName}" );
        var r3 = await DispatchAsync( $"WHEN VERSION >= '99.0' CREATE INDEX {_indexName}" );

        Assert.AreEqual( StatementOutcome.Executed, r1.Outcome );
        Assert.AreEqual( StatementOutcome.Executed, r2.Outcome );
        Assert.AreEqual( StatementOutcome.Skipped, r3.Outcome );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task WhenVersion_ReportsClusterVersionInSkipDetail()
    {
        var result = await DispatchAsync( $"WHEN VERSION >= '99.0' CREATE INDEX {_indexName}" );

        // Detail should include the actual cluster version, not just "false".
        // Production diagnosis depends on this — without the actual version
        // in the log, ops can't distinguish "cluster is older than expected"
        // from "predicate is wrong".
        StringAssert.Matches( result.Detail!, new System.Text.RegularExpressions.Regex( @"cluster \d+\.\d+" ) );
    }
}
#endif
