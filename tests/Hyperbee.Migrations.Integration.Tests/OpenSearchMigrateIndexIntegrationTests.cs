//#define INTEGRATIONS
#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
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
// Phase 2 Slice 2.3 — MIGRATE INDEX composite verb integration tests against
// real OpenSearch. The composite expands at parse time to:
//   1. CREATE INDEX <new>  (body from runtime _index_template/<id> fetch)
//   2. REINDEX FROM <old> TO <new>  (with op_type:create injected)
//   3. ALIAS SWAP <alias> FROM <old> TO <new>  (when VIA ALIAS present)
//
// Coverage:
//   - Template resolution at runtime (TemplateResolutionMiddleware)
//   - Composite dispatch halt-on-failure semantics
//   - R-24c (o) keystone: composite produces identical end-state to the
//     hand-composed CREATE+REINDEX+ALIAS-SWAP sequence

[TestClass]
public class OpenSearchMigrateIndexIntegrationTests
{
    private OpenSearchStatementParser _parser = null!;
    private StatementDispatcher _dispatcher = null!;
    private OpenSearchMigrationOptions _options = null!;
    private string _slug = null!;
    private string _src = null!;
    private string _dst = null!;
    private string _alias = null!;
    private string _templateName = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _parser = new OpenSearchStatementParser();
        _dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        _options = new OpenSearchMigrationOptions { WaitMode = WaitMode.Off };

        _slug = Guid.NewGuid().ToString( "n" );
        _src = $"users-v1-{_slug}";
        _dst = $"users-v2-{_slug}";
        _alias = $"users-current-{_slug}";
        _templateName = $"tpl-{_slug}";

        // Pre-create the template that MIGRATE INDEX will resolve at runtime.
        var templateBody = JsonNode.Parse( $$"""
            {
              "index_patterns": ["users-v2-{{_slug}}"],
              "template": {
                "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
                "mappings": {
                  "properties": {
                    "id":   { "type": "keyword" },
                    "name": { "type": "text" },
                    "tier": { "type": "keyword" }
                  }
                }
              },
              "priority": 100
            }
            """ );
        await DispatchAsync( $"CREATE TEMPLATE {_templateName} WITH BODY $body", templateBody );

        // Pre-create the source index directly via the low-level client. We
        // bypass `CREATE INDEX` here because that path injects `dynamic: strict`
        // (R-17) and we want a permissive source schema so seeding succeeds.
        await CreatePermissiveIndexAsync( _src );
        await SeedSourceDocsAsync( _src, count: 5 );
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        await ll.Indices.DeleteAsync<StringResponse>( $"{_src},{_dst}" );
        await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.DELETE, $"_index_template/{_templateName}", default );
    }

    private Task<StatementResult> DispatchAsync( string statement, JsonNode? body = null )
    {
        var ast = _parser.Parse( statement );
        var ctx = new StatementContext
        {
            Client = OpenSearchTestContainer.Client,
            Options = _options,
            TimeProvider = TimeProvider.System,
            Logger = NullLogger.Instance,
            ResolvedBody = body,
            CancellationToken = default
        };
        return _dispatcher.DispatchAsync( ast, ctx );
    }

    private static async Task CreatePermissiveIndexAsync( string indexName )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        const string body = """
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
              "mappings": {
                "properties": {
                  "id":   { "type": "keyword" },
                  "name": { "type": "text" },
                  "tier": { "type": "keyword" }
                }
              }
            }
            """;
        var resp = await ll.Indices.CreateAsync<StringResponse>(
            indexName, PostData.String( body ) );
        if ( !resp.Success )
            throw new InvalidOperationException( $"failed to create test source index: {resp.Body}" );
    }

    private static async Task SeedSourceDocsAsync( string indexName, int count )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        for ( var i = 0; i < count; i++ )
        {
            var doc = $$"""{ "id": "u{{i}}", "name": "user{{i}}", "tier": "gold" }""";
            await ll.IndexAsync<StringResponse>( indexName, $"u{i}", PostData.String( doc ) );
        }
        // refresh so the reindex sees them
        await ll.Indices.RefreshAsync<StringResponse>( indexName );
    }

    private static async Task<int> CountDocsAsync( string index )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        await ll.Indices.RefreshAsync<StringResponse>( index );
        var resp = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.GET, $"{index}/_count", default );
        if ( !resp.Success ) return -1;
        using var doc = JsonDocument.Parse( resp.Body );
        return doc.RootElement.GetProperty( "count" ).GetInt32();
    }

    private static async Task<string?> ResolveAliasIndexAsync( string aliasName )
    {
        var ll = OpenSearchTestContainer.LowLevelClient;
        var resp = await ll.Indices.GetAliasAsync<StringResponse>( aliasName );
        if ( !resp.Success ) return null;
        using var doc = JsonDocument.Parse( resp.Body! );
        // body is { "<index-name>": { "aliases": { "<alias>": {} } } }
        foreach ( var prop in doc.RootElement.EnumerateObject() )
            return prop.Name;
        return null;
    }

    // ---- happy path ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task MigrateIndex_WithTemplateAndAlias_ProducesExpectedEndState()
    {
        // Real-world usage: the application reads via an alias that already
        // points to the source index. MIGRATE INDEX swaps that alias to the
        // newly-created destination once reindex completes.
        await DispatchAsync( $"ALIAS ADD {_alias} ON {_src}" );

        var result = await DispatchAsync(
            $"MIGRATE INDEX {_src} TO {_dst} WITH TEMPLATE {_templateName} VIA ALIAS {_alias}" );

        Assert.IsTrue( result.IsSuccess, $"composite failed: {result.Detail}" );
        Assert.AreEqual( "MIGRATE INDEX", result.Verb );

        // Destination index exists, has the seeded docs reindexed, alias swapped.
        var dstCount = await CountDocsAsync( _dst );
        Assert.AreEqual( 5, dstCount, "destination should contain reindexed docs" );

        var aliasIndex = await ResolveAliasIndexAsync( _alias );
        Assert.AreEqual( _dst, aliasIndex, "alias should resolve to destination" );

        // Verify the destination's mappings came from the template.
        var ll = OpenSearchTestContainer.LowLevelClient;
        var mapping = await ll.Indices.GetMappingAsync<StringResponse>( _dst );
        Assert.IsTrue( mapping.Success );
        StringAssert.Contains( mapping.Body!, "\"tier\":{\"type\":\"keyword\"}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task MigrateIndex_WithoutAlias_SkipsSwap()
    {
        // No VIA ALIAS — composite is just CREATE + REINDEX. Author retains
        // cutover responsibility (R-30).
        var result = await DispatchAsync(
            $"MIGRATE INDEX {_src} TO {_dst} WITH TEMPLATE {_templateName}" );

        Assert.IsTrue( result.IsSuccess, $"composite failed: {result.Detail}" );

        var dstCount = await CountDocsAsync( _dst );
        Assert.AreEqual( 5, dstCount );

        // No alias was swapped — looking up the alias name should 404.
        var ll = OpenSearchTestContainer.LowLevelClient;
        var aliasResp = await ll.Indices.GetAliasAsync<StringResponse>( _alias );
        Assert.AreEqual( 404, aliasResp.HttpStatusCode );
    }

    // ---- equivalence (R-24c keystone) ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-24c" )]
    public async Task MigrateIndex_ProducesIdenticalEndState_ToHandComposedSequence()
    {
        // R-24c (o): the composite verb must produce the same end state as
        // the four-statement hand-composed sequence. We can't actually run
        // both against the same starting state, so we run two parallel
        // pipelines on disjoint suffixed indices and compare:
        //   - destination doc count
        //   - destination mappings
        //   - alias resolution
        //
        // We seed the same data on both source indices so the post-condition
        // is comparable.

        var altSrc = $"alt-{_src}";
        var altDst = $"alt-{_dst}";
        var altAlias = $"alt-{_alias}";

        // Set up the parallel pipeline (alt-) with hand-composed statements
        await CreatePermissiveIndexAsync( altSrc );
        await SeedSourceDocsAsync( altSrc, count: 5 );
        var ll = OpenSearchTestContainer.LowLevelClient;

        // Resolve the template body the same way the runtime middleware would
        // and use it as the inline body for the hand-composed CREATE.
        var resolution = await new TemplateResolutionMiddleware()
            .ResolveAsync( ll, new TemplateBodyRef( _templateName ), default );
        Assert.IsNotNull( resolution.Body, "template should resolve to a body" );

        try
        {
            // Hand-composed: CREATE INDEX (with resolved body) + REINDEX + ALIAS SWAP
            var altCreate = await DispatchAsync( $"CREATE INDEX {altDst} WITH BODY $body", resolution.Body );
            Assert.IsTrue( altCreate.IsSuccess, $"alt CREATE failed: {altCreate.Detail}" );

            var altReindex = await DispatchAsync( $"REINDEX FROM {altSrc} TO {altDst}" );
            Assert.IsTrue( altReindex.IsSuccess, $"alt REINDEX failed: {altReindex.Detail}" );

            // Pre-bind the alt alias to its source so the swap has something
            // to remove (the composite path swaps from <src>; in the hand-
            // composed alt-pipeline we mirror that by binding altAlias to altSrc
            // first).
            await DispatchAsync( $"ALIAS ADD {altAlias} ON {altSrc}" );

            var altSwap = await DispatchAsync( $"ALIAS SWAP {altAlias} FROM {altSrc} TO {altDst}" );
            Assert.IsTrue( altSwap.IsSuccess, $"alt SWAP failed: {altSwap.Detail}" );

            // Composite path: the standard MIGRATE INDEX run uses the existing
            // _src/_dst/_alias from Setup. We need to also pre-bind _alias to
            // _src so the SWAP inside MIGRATE INDEX has the precondition met.
            await DispatchAsync( $"ALIAS ADD {_alias} ON {_src}" );

            var compResult = await DispatchAsync(
                $"MIGRATE INDEX {_src} TO {_dst} WITH TEMPLATE {_templateName} VIA ALIAS {_alias}" );
            Assert.IsTrue( compResult.IsSuccess, $"composite failed: {compResult.Detail}" );

            // ---- compare end states ----

            var compCount = await CountDocsAsync( _dst );
            var altCount = await CountDocsAsync( altDst );
            Assert.AreEqual( compCount, altCount, "destination doc counts diverge" );

            var compMapping = await ll.Indices.GetMappingAsync<StringResponse>( _dst );
            var altMapping = await ll.Indices.GetMappingAsync<StringResponse>( altDst );
            Assert.IsTrue( compMapping.Success );
            Assert.IsTrue( altMapping.Success );

            // Mapping bodies are wrapped under the index name; extract the inner
            // mappings for a name-agnostic comparison.
            var compMappingsNode = JsonNode.Parse( compMapping.Body! )?[_dst]?["mappings"];
            var altMappingsNode = JsonNode.Parse( altMapping.Body! )?[altDst]?["mappings"];
            Assert.AreEqual(
                compMappingsNode?.ToJsonString(),
                altMappingsNode?.ToJsonString(),
                "destination mappings diverge between composite and hand-composed paths" );

            var compAliasIdx = await ResolveAliasIndexAsync( _alias );
            var altAliasIdx = await ResolveAliasIndexAsync( altAlias );
            Assert.AreEqual( _dst, compAliasIdx, "composite alias did not resolve to its destination" );
            Assert.AreEqual( altDst, altAliasIdx, "alt alias did not resolve to its destination" );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( $"{altSrc},{altDst}" );
        }
    }

    // ---- failure semantics ----

    // ---- composed_of-aware refinement (R-17) ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    [TestCategory( "R-17" )]
    public async Task MigrateIndex_TemplateUsesComposedOf_SkipsDynamicStrictInjection()
    {
        // R-17 refinement: when the source template references components via
        // composed_of, the MIGRATE INDEX path must NOT inject dynamic:strict
        // into the resolved body — same semantics as the inline-body skip in
        // SafeDefaultMergeMiddleware, lifted to the runtime-resolved path.
        //
        // Verification: write a document with a field NOT declared in the
        // template's mappings AFTER the migrate. With dynamic:strict, the
        // cluster rejects with strict_dynamic_mapping_exception. Without it
        // (cluster default dynamic:true), the field is accepted and a new
        // mapping is auto-created.

        var ll = OpenSearchTestContainer.LowLevelClient;
        var componentName = $"comp-{_slug}";
        var composedTemplateName = $"composed-{_slug}";
        var composedDst = $"composed-dst-{_slug}";

        // Pre-create a component template so the composed-of reference
        // resolves cluster-side (the cluster validates references on PUT of
        // the parent index template).
        var componentBody = """
            {
              "template": {
                "mappings": {
                  "properties": {
                    "id": { "type": "keyword" }
                  }
                }
              }
            }
            """;
        await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.PUT,
            $"_component_template/{componentName}",
            default,
            data: PostData.String( componentBody ) );

        // Parent template that uses composed_of
        var composedBody = $$"""
            {
              "index_patterns": ["composed-dst-{{_slug}}"],
              "composed_of": ["{{componentName}}"],
              "template": {
                "settings": { "number_of_shards": 1, "number_of_replicas": 0 }
              },
              "priority": 200
            }
            """;
        await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.PUT,
            $"_index_template/{composedTemplateName}",
            default,
            data: PostData.String( composedBody ) );

        try
        {
            // Run MIGRATE INDEX against the composed_of template
            var result = await DispatchAsync(
                $"MIGRATE INDEX {_src} TO {composedDst} WITH TEMPLATE {composedTemplateName}" );
            Assert.IsTrue( result.IsSuccess, $"composite failed: {result.Detail}" );

            // Write a document with a field NOT in the template's mappings.
            // If dynamic:strict was injected (the bug we're fixing), the
            // cluster rejects with strict_dynamic_mapping_exception. With
            // the fix, the cluster accepts (default dynamic:true).
            var doc = """{ "id": "x1", "completely_new_field": "value" }""";
            var indexResp = await ll.IndexAsync<StringResponse>(
                composedDst, "x1", PostData.String( doc ) );

            Assert.IsTrue( indexResp.Success,
                $"writing un-mapped field should succeed when composed_of is detected " +
                $"(dynamic:strict must be skipped); got HTTP {indexResp.HttpStatusCode}: {indexResp.Body}" );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( composedDst );
            await ll.DoRequestAsync<StringResponse>(
                OpenSearch.Net.HttpMethod.DELETE,
                $"_index_template/{composedTemplateName}", default );
            await ll.DoRequestAsync<StringResponse>(
                OpenSearch.Net.HttpMethod.DELETE,
                $"_component_template/{componentName}", default );
        }
    }

    // ---- failure semantics ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task MigrateIndex_TemplateNotFound_FailsAtCreateStep()
    {
        // R-30: missing template surfaces with the index-template name in the
        // error. Composite halts at the first failing child (CREATE INDEX).
        var result = await DispatchAsync(
            $"MIGRATE INDEX {_src} TO {_dst} WITH TEMPLATE does-not-exist-{_slug} VIA ALIAS {_alias}" );

        Assert.IsFalse( result.IsSuccess );
        StringAssert.Contains( result.Detail!, $"does-not-exist-{_slug}" );
        StringAssert.Contains( result.Detail!, "halted at child 1" );

        // Destination index should not exist (composite halted before CREATE
        // succeeded, but actually the resolver throws before the CREATE call
        // so no index is created). Reindex/swap should not have run either.
        var ll = OpenSearchTestContainer.LowLevelClient;
        var headDst = await ll.Indices.ExistsAsync<StringResponse>( _dst );
        Assert.AreEqual( 404, headDst.HttpStatusCode );
    }
}
#endif
