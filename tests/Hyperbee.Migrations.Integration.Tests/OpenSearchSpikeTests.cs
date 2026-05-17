//#define INTEGRATIONS
#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Phase 0 Task 0.6 — wire-level spike tests against real OpenSearch.
//
// These tests fire the Phase 0 kill criterion (per assessment 0003 / A8):
// "Merge logic cannot deterministically produce expected JSON without
//  ambiguity for any of the 5 documented edge cases."
//
// Each test parses a statement via OpenSearchStatementParser, resolves a
// representative body, applies SafeDefaultMergeMiddleware, dispatches via
// the OpenSearchLowLevelClient (which has DisableDirectStreaming so the
// captured request body is preserved on the response audit), and asserts
// the wire-level body matches expectations. For round-trip tests, the
// destination cluster state is also queried.
//
// If any of these tests fail in a way that requires adding a new AST flag
// to resolve ambiguity (per the Phase 0 kill criterion), STOP and revisit
// ADR-0011. Approach A (runtime-middleware-only) is the documented
// fallback; AST + grammar code from Task 0.5 remains reusable.

[TestClass]
// LocalOnly: heavy container-based integration test; excluded from the gating CI matrix (does not gate the NuGet publish). Runs locally / on demand.
[TestCategory( "LocalOnly" )]
public class OpenSearchSpikeTests
{
    private OpenSearchStatementParser _parser = null!;
    private SafeDefaultMergeMiddleware _middleware = null!;
    private OpenSearchLowLevelClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _parser = new OpenSearchStatementParser();
        _middleware = new SafeDefaultMergeMiddleware();
        _client = OpenSearchTestContainer.LowLevelClient;
    }

    private static string Bytes( StringResponse response )
        => System.Text.Encoding.UTF8.GetString( response.ApiCall.RequestBodyInBytes ?? [] );

    private static string MakeIndexName( string baseName )
        => $"{baseName}-{Guid.NewGuid():N}".ToLowerInvariant();

    private static JsonNode? ParseJson( string json )
        => JsonNode.Parse( json );

    // ---- 5 CREATE INDEX edge cases ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task CreateIndex_FlatBody_NoMappings_InjectsDynamicStrict_OnTheWire()
    {
        var name = MakeIndexName( "users" );
        var ast = (CreateIndexAst) _parser.Parse( $"CREATE INDEX {name} WITH BODY $body" );
        var body = ParseJson( """ { "settings": { "number_of_shards": 1, "number_of_replicas": 0 } } """ );
        var merged = _middleware.Merge( ast, body );

        var response = await _client.Indices.CreateAsync<StringResponse>(
            name,
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( response.Success, $"Create failed: {response.Body}" );

        var sentBody = Bytes( response );
        StringAssert.Contains( sentBody, "\"dynamic\":\"strict\"", "Wire body must include dynamic:strict injection." );

        // Cleanup
        await _client.Indices.DeleteAsync<StringResponse>( name );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task CreateIndex_BodyWithExplicitDynamicTrue_PreservesUserValue_OnTheWire()
    {
        var name = MakeIndexName( "users" );
        var ast = (CreateIndexAst) _parser.Parse( $"CREATE INDEX {name} WITH BODY $body" );
        var body = ParseJson( """
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
              "mappings": { "dynamic": "true", "properties": { "id": { "type": "keyword" } } }
            }
            """ );
        var merged = _middleware.Merge( ast, body );

        var response = await _client.Indices.CreateAsync<StringResponse>(
            name,
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( response.Success, $"Create failed: {response.Body}" );

        var sentBody = Bytes( response );
        StringAssert.Contains( sentBody, "\"dynamic\":\"true\"", "User-explicit dynamic:true must be preserved." );

        await _client.Indices.DeleteAsync<StringResponse>( name );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public void CreateIndex_BodyWithComposedOf_SkipsInjection_AtMergeLayer()
    {
        // CORRECTION discovered during integration validation: `composed_of` is a
        // PUT /_index_template/<name> field — NOT a valid PUT /<index> field.
        // OpenSearch returns 400 "unknown key [composed_of] for create index" when
        // we try to send composed_of in a direct index-create body.
        //
        // This means the merge-layer composed_of skip in SafeDefaultMergeMiddleware
        // is actually defensive code that shields the user from a body shape
        // the cluster rejects anyway. PM-4's risk surface (clobbering a
        // component-template-defined dynamic:false) lives in the CREATE TEMPLATE /
        // CREATE COMPONENT verb path (Phase 2), where composed_of IS a valid field.
        //
        // For now, we verify the merge-layer skip behavior in isolation rather
        // than trying to send composed_of to the cluster.

        var name = MakeIndexName( "users" );
        var ast = (CreateIndexAst) _parser.Parse( $"CREATE INDEX {name} WITH BODY $body" );
        var body = ParseJson( """
            {
              "composed_of": ["some-component"],
              "settings": { "number_of_shards": 1, "number_of_replicas": 0 }
            }
            """ );
        var merged = _middleware.Merge( ast, body );

        var sentBody = merged.ToJsonString();
        StringAssert.Contains( sentBody, "composed_of", "Merged body must preserve composed_of." );
        Assert.DoesNotContain( "\"dynamic\":\"strict\"", sentBody,
            "composed_of bodies must NOT have dynamic:strict injected (R-17 / PM-4)." );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task CreateIndex_BodyWithMappingsPropertiesOnly_AddsDynamicStrictAlongsideProperties()
    {
        var name = MakeIndexName( "users" );
        var ast = (CreateIndexAst) _parser.Parse( $"CREATE INDEX {name} WITH BODY $body" );
        var body = ParseJson( """
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
              "mappings": { "properties": { "id": { "type": "keyword" } } }
            }
            """ );
        var merged = _middleware.Merge( ast, body );

        var response = await _client.Indices.CreateAsync<StringResponse>(
            name,
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( response.Success, $"Create failed: {response.Body}" );

        var sentBody = Bytes( response );
        StringAssert.Contains( sentBody, "\"dynamic\":\"strict\"" );
        StringAssert.Contains( sentBody, "\"properties\"" );

        await _client.Indices.DeleteAsync<StringResponse>( name );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task CreateIndex_BodyWithSettingsOnly_CreatesMappingsBlockWithDynamicStrict()
    {
        var name = MakeIndexName( "users" );
        var ast = (CreateIndexAst) _parser.Parse( $"CREATE INDEX {name} WITH BODY $body" );
        var body = ParseJson( """ { "settings": { "number_of_shards": 1, "number_of_replicas": 0 } } """ );
        var merged = _middleware.Merge( ast, body );

        var response = await _client.Indices.CreateAsync<StringResponse>(
            name,
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( response.Success, $"Create failed: {response.Body}" );

        var sentBody = Bytes( response );
        StringAssert.Contains( sentBody, "\"mappings\":" );
        StringAssert.Contains( sentBody, "\"dynamic\":\"strict\"" );

        await _client.Indices.DeleteAsync<StringResponse>( name );
    }

    // ---- 5 REINDEX edge cases ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task Reindex_NoBody_BuildsFullPayloadWithOpTypeCreate_OnTheWire()
    {
        var src = MakeIndexName( "users-v1" );
        var dst = MakeIndexName( "users-v2" );

        await CreateMinimalIndex( src );
        await CreateMinimalIndex( dst );

        var ast = (ReindexAst) _parser.Parse( $"REINDEX FROM {src} TO {dst}" );
        var merged = _middleware.Merge( ast, body: null );

        var response = await _client.ReindexOnServerAsync<StringResponse>(
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( response.Success, $"Reindex failed: {response.Body}" );

        var sentBody = Bytes( response );
        StringAssert.Contains( sentBody, "\"op_type\":\"create\"", "Bare REINDEX must inject op_type:create." );
        StringAssert.Contains( sentBody, $"\"index\":\"{src}\"" );
        StringAssert.Contains( sentBody, $"\"index\":\"{dst}\"" );

        await _client.Indices.DeleteAsync<StringResponse>( $"{src},{dst}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task Reindex_BodyWithDestObject_AddsOpTypeCreate_OnTheWire()
    {
        var src = MakeIndexName( "users-v1" );
        var dst = MakeIndexName( "users-v2" );

        await CreateMinimalIndex( src );
        await CreateMinimalIndex( dst );

        var ast = (ReindexAst) _parser.Parse( $"REINDEX FROM {src} TO {dst} WITH BODY $body" );
        var body = ParseJson( $$"""
            {
              "source": { "index": "{{src}}", "query": { "match_all": {} } },
              "dest": { "index": "{{dst}}" }
            }
            """ );
        var merged = _middleware.Merge( ast, body );

        var response = await _client.ReindexOnServerAsync<StringResponse>(
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( response.Success, $"Reindex failed: {response.Body}" );

        var sentBody = Bytes( response );
        StringAssert.Contains( sentBody, "\"op_type\":\"create\"" );
        StringAssert.Contains( sentBody, "\"match_all\"", "User query field must be preserved." );

        await _client.Indices.DeleteAsync<StringResponse>( $"{src},{dst}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public void Reindex_BodyWithExplicitOpTypeIndex_FailsBeforeWireDispatch()
    {
        var src = MakeIndexName( "users-v1" );
        var dst = MakeIndexName( "users-v2" );

        var ast = (ReindexAst) _parser.Parse( $"REINDEX FROM {src} TO {dst} WITH BODY $body" );
        var body = ParseJson( $$"""
            {
              "source": { "index": "{{src}}" },
              "dest": { "index": "{{dst}}", "op_type": "index" }
            }
            """ );

        var ex = Assert.ThrowsExactly<SafeDefaultConflictException>( () => _middleware.Merge( ast, body ) );
        StringAssert.Contains( ex.Message, "UNSAFE", "Error must point to UNSAFE remediation per R-18." );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task Reindex_BodyWithExplicitOpTypeCreate_IsIdempotent_OnTheWire()
    {
        var src = MakeIndexName( "users-v1" );
        var dst = MakeIndexName( "users-v2" );

        await CreateMinimalIndex( src );
        await CreateMinimalIndex( dst );

        var ast = (ReindexAst) _parser.Parse( $"REINDEX FROM {src} TO {dst} WITH BODY $body" );
        var body = ParseJson( $$"""
            {
              "source": { "index": "{{src}}" },
              "dest": { "index": "{{dst}}", "op_type": "create" }
            }
            """ );
        var merged = _middleware.Merge( ast, body );

        var response = await _client.ReindexOnServerAsync<StringResponse>(
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( response.Success, $"Reindex failed: {response.Body}" );

        var sentBody = Bytes( response );
        // Exactly one op_type:create — middleware did not add a duplicate
        var matches = System.Text.RegularExpressions.Regex.Matches( sentBody, "\"op_type\":\"create\"" );
        Assert.HasCount( 1, matches, "Idempotent injection must produce exactly one op_type:create." );

        await _client.Indices.DeleteAsync<StringResponse>( $"{src},{dst}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Spike" )]
    public async Task Reindex_RoundTrip_OpTypeCreate_PreventsDoubleWrite()
    {
        // The keystone test for ADR-0011 + Phase 0 kill criterion.
        //
        // Scenario: A previous reindex run got partway through; dst contains some docs
        // that already exist (same _id as in src). The new run must NOT double-write or
        // lose data — op_type:create makes this safe by SKIPPING any doc that already
        // exists with the target _id (logged as version_conflict but the run continues).

        var src = MakeIndexName( "users-v1" );
        var dst = MakeIndexName( "users-v2" );

        await CreateMinimalIndex( src );
        await CreateMinimalIndex( dst );

        // Seed source with 3 docs
        for ( var i = 1; i <= 3; i++ )
        {
            await _client.IndexAsync<StringResponse>(
                src, i.ToString(),
                PostData.String( $$"""{ "id": "{{i}}", "version": "v1" }""" ),
                new IndexRequestParameters { Refresh = Refresh.True } );
        }

        // Pre-seed dst with one doc that has the SAME _id as src/2 (simulating partial prior run)
        await _client.IndexAsync<StringResponse>(
            dst, "2",
            PostData.String( """{ "id": "2", "version": "v2-partial" }""" ),
            new IndexRequestParameters { Refresh = Refresh.True } );

        // Now run REINDEX with op_type:create injection (default).
        // Add `conflicts: "proceed"` so OpenSearch returns the version_conflicts
        // count in a 200 response instead of aborting at the first conflict
        // (the default `conflicts: "abort"` would surface as 409 with no body
        // detail). For real migrations, `conflicts: "proceed"` is the desired
        // mode — partial-run retries should continue past pre-existing docs.
        // Whether the safe-default merge should also inject conflicts:proceed
        // is a Phase 2 design question.
        var ast = (ReindexAst) _parser.Parse( $"REINDEX FROM {src} TO {dst}" );
        var body = ParseJson( $$"""
            {
              "conflicts": "proceed",
              "source": { "index": "{{src}}" },
              "dest": { "index": "{{dst}}" }
            }
            """ );
        var merged = _middleware.Merge( ast, body );

        var reindexResponse = await _client.ReindexOnServerAsync<StringResponse>(
            PostData.String( merged.ToJsonString() ) );

        Assert.IsTrue( reindexResponse.Success, $"Reindex failed: {reindexResponse.Body}" );

        // version_conflicts in the response indicate op_type:create skipped the pre-existing doc
        StringAssert.Contains( reindexResponse.Body, "\"version_conflicts\":1",
            "op_type:create must skip pre-existing dst docs (1 conflict expected for the partial-prior-run doc)." );

        // Refresh and verify dst has 3 docs total, NOT 4 (no double-write)
        await _client.Indices.RefreshAsync<StringResponse>( dst );
        var countResponse = await _client.CountAsync<StringResponse>( dst, PostData.String( "{}" ) );
        var count = JsonDocument.Parse( countResponse.Body ).RootElement.GetProperty( "count" ).GetInt32();
        Assert.AreEqual( 3, count, "Destination must have 3 docs (no double-write of the pre-existing one)." );

        // Verify the pre-existing doc kept its v2-partial value (op_type:create did NOT overwrite)
        var getResponse = await _client.GetAsync<StringResponse>( dst, "2" );
        StringAssert.Contains( getResponse.Body, "v2-partial",
            "Pre-existing doc must be preserved by op_type:create (no overwrite)." );

        await _client.Indices.DeleteAsync<StringResponse>( $"{src},{dst}" );
    }

    private async Task CreateMinimalIndex( string name )
    {
        var response = await _client.Indices.CreateAsync<StringResponse>(
            name,
            PostData.String( """ { "settings": { "number_of_shards": 1, "number_of_replicas": 0 } } """ ) );
        Assert.IsTrue( response.Success, $"Create {name} failed: {response.Body}" );
    }
}
#endif
