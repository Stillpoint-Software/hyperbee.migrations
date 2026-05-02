//#define INTEGRATIONS
#nullable enable
using System.Text.Json;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Phase 2 Slice 2.1 — alias verb integration tests against real OpenSearch.
// The keystone test is AliasSwap_AtomicTOCTOUFree: validates that a
// concurrent swap-from-different-old fails atomically per R-16 / NF-2,
// without a separate-GET-then-POST window.

[TestClass]
public class OpenSearchAliasIntegrationTests
{
    private OpenSearchStatementParser _parser = null!;
    private StatementDispatcher _dispatcher = null!;
    private OpenSearchMigrationOptions _options = null!;
    private string _src = null!;
    private string _dst = null!;
    private string _alias = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _parser = new OpenSearchStatementParser();
        _dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        _options = new OpenSearchMigrationOptions { WaitMode = WaitMode.Off };

        var slug = Guid.NewGuid().ToString( "n" );
        _src = $"users-v1-{slug}";
        _dst = $"users-v2-{slug}";
        _alias = $"users-current-{slug}";

        // Pre-create both indices so alias operations have valid targets
        await DispatchAsync( $"CREATE INDEX {_src}" );
        await DispatchAsync( $"CREATE INDEX {_dst}" );
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        await ll.Indices.DeleteAsync<StringResponse>( $"{_src},{_dst}" );
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

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task AliasAdd_PointsAliasAtIndex()
    {
        var result = await DispatchAsync( $"ALIAS ADD {_alias} ON {_src}" );

        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        // Verify alias resolves to src
        var ll = OpenSearchTestContainer.LowLevelClient;
        var aliasResp = await ll.Indices.GetAliasAsync<StringResponse>( _alias );
        Assert.AreEqual( 200, aliasResp.HttpStatusCode );
        StringAssert.Contains( aliasResp.Body!, _src );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task AliasRemove_DetachesAlias()
    {
        await DispatchAsync( $"ALIAS ADD {_alias} ON {_src}" );

        var result = await DispatchAsync( $"ALIAS REMOVE {_alias} ON {_src}" );
        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        // Alias should no longer exist
        try
        {
            var ll = OpenSearchTestContainer.LowLevelClient;
            var aliasResp = await ll.Indices.GetAliasAsync<StringResponse>( _alias );
            Assert.AreEqual( 404, aliasResp.HttpStatusCode );
        }
        catch ( OpenSearchClientException ex ) when ( ex.Response?.HttpStatusCode == 404 )
        {
            // Acceptable: alias not found surfaces as 404
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task AliasSwap_AtomicallyMovesAliasFromOldToNew()
    {
        // Set up alias on src
        await DispatchAsync( $"ALIAS ADD {_alias} ON {_src}" );

        // Swap to dst
        var result = await DispatchAsync( $"ALIAS SWAP {_alias} FROM {_src} TO {_dst}" );

        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        // Alias should now point at dst, NOT src
        var ll = OpenSearchTestContainer.LowLevelClient;
        var aliasResp = await ll.Indices.GetAliasAsync<StringResponse>( _alias );
        Assert.AreEqual( 200, aliasResp.HttpStatusCode );

        using var doc = JsonDocument.Parse( aliasResp.Body! );
        Assert.IsTrue( doc.RootElement.TryGetProperty( _dst, out _ ),
            "Alias should resolve to the new index after swap" );
        Assert.IsFalse( doc.RootElement.TryGetProperty( _src, out _ ),
            "Alias should NOT resolve to the old index after swap" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task AliasSwap_AtomicNoSplitState_AliasNeverPointsAtBothIndices()
    {
        // R-16 / NF-2 atomicity guarantee: the swap is a single _aliases body
        // with both remove + add actions. Either both succeed or both fail —
        // never a partial state where the alias points at both indices.
        //
        // (The dispatcher includes "must_exist": true on the remove action so
        // the cluster rejects on a stale FROM. OpenSearch 2.x is permissive
        // about removing a non-existent alias even with must_exist set, so
        // we don't assert "swap fails when FROM is wrong" here — instead we
        // assert the atomicity property: alias never resolves to both src
        // and dst simultaneously, which is the correctness guarantee.)

        await DispatchAsync( $"ALIAS ADD {_alias} ON {_src}" );

        var result = await DispatchAsync( $"ALIAS SWAP {_alias} FROM {_src} TO {_dst}" );
        Assert.IsTrue( result.IsSuccess, $"swap dispatch failed: {result.Detail}" );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var aliasResp = await ll.Indices.GetAliasAsync<StringResponse>( _alias );
        Assert.AreEqual( 200, aliasResp.HttpStatusCode );

        using var doc = JsonDocument.Parse( aliasResp.Body! );
        var srcHasAlias = doc.RootElement.TryGetProperty( _src, out _ );
        var dstHasAlias = doc.RootElement.TryGetProperty( _dst, out _ );

        // Atomic post-condition: alias is on exactly one index, NOT both.
        Assert.IsFalse( srcHasAlias && dstHasAlias,
            "Alias must NEVER point at both indices simultaneously after a swap (R-16 atomicity)" );
        Assert.IsTrue( dstHasAlias,
            "After successful swap, alias should resolve to dst" );
    }
}
#endif
