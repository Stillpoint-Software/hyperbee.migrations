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
// Phase 2 Slice 2.2 — template, component, and ISM policy verb integration
// tests against real OpenSearch.
//
// Coverage:
//   - CREATE/DROP TEMPLATE (composable index template)
//   - CREATE/DROP COMPONENT (component template)
//   - CREATE POLICY (ISM policy)
//   - APPLY POLICY (attach to existing indices)
//   - IF EXISTS guards on DROP

// ISM CREATE POLICY operations bootstrap the shared `.opendistro-ism-config`
// system index on first use; concurrent tests race that single create and the
// second one sees HTTP 409 ("index already exists"). Run sequentially so the
// implicit ISM bootstrap happens once, deterministically.
[TestClass]
[DoNotParallelize]
public class OpenSearchTemplatePolicyIntegrationTests
{
    private OpenSearchStatementParser _parser = null!;
    private StatementDispatcher _dispatcher = null!;
    private OpenSearchMigrationOptions _options = null!;
    private string _slug = null!;
    private string _templateName = null!;
    private string _componentName = null!;
    private string _policyId = null!;
    private string _indexName = null!;
    private string _indexPattern = null!;

    [TestInitialize]
    public void Setup()
    {
        _parser = new OpenSearchStatementParser();
        _dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        _options = new OpenSearchMigrationOptions { WaitMode = WaitMode.Off };

        _slug = Guid.NewGuid().ToString( "n" );
        _templateName = $"tpl-{_slug}";
        _componentName = $"comp-{_slug}";
        _policyId = $"pol-{_slug}";
        _indexName = $"logs-{_slug}-2026.01.01";
        _indexPattern = $"logs-{_slug}-*";
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        var ll = OpenSearchTestContainer.LowLevelClient;

        // best-effort cleanup; tolerate 404s
        await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.DELETE, $"_index_template/{_templateName}", default );
        await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.DELETE, $"_component_template/{_componentName}", default );
        await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.DELETE, $"_plugins/_ism/policies/{_policyId}", default );
        await ll.Indices.DeleteAsync<StringResponse>( _indexName );
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

    // ---- CREATE TEMPLATE ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task CreateTemplate_PutsCompostableIndexTemplate()
    {
        var body = JsonNode.Parse( $$"""
            {
              "index_patterns": ["{{_indexPattern}}"],
              "template": {
                "settings": { "number_of_shards": 1, "number_of_replicas": 0 }
              },
              "priority": 100
            }
            """ );

        var result = await DispatchAsync( $"CREATE TEMPLATE {_templateName} WITH BODY $body", body );
        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var get = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.GET, $"_index_template/{_templateName}", default );
        Assert.AreEqual( 200, get.HttpStatusCode );
        StringAssert.Contains( get.Body!, _indexPattern );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task CreateTemplate_NullBody_Fails()
    {
        var result = await DispatchAsync( $"CREATE TEMPLATE {_templateName}", body: null );
        Assert.IsFalse( result.IsSuccess );
        StringAssert.Contains( result.Detail!, "WITH BODY" );
    }

    // ---- CREATE COMPONENT ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task CreateComponent_PutsComponentTemplate()
    {
        var body = JsonNode.Parse( """
            {
              "template": {
                "mappings": {
                  "properties": {
                    "@timestamp": { "type": "date" }
                  }
                }
              }
            }
            """ );

        var result = await DispatchAsync( $"CREATE COMPONENT {_componentName} WITH BODY $body", body );
        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var get = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.GET, $"_component_template/{_componentName}", default );
        Assert.AreEqual( 200, get.HttpStatusCode );
        StringAssert.Contains( get.Body!, "@timestamp" );
    }

    // ---- DROP TEMPLATE / COMPONENT ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DropTemplate_RemovesTemplate()
    {
        var body = JsonNode.Parse( $$"""
            { "index_patterns": ["{{_indexPattern}}"], "priority": 100 }
            """ );
        await DispatchAsync( $"CREATE TEMPLATE {_templateName} WITH BODY $body", body );

        var result = await DispatchAsync( $"DROP TEMPLATE {_templateName}" );
        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var head = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.HEAD, $"_index_template/{_templateName}", default );
        Assert.AreEqual( 404, head.HttpStatusCode );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DropTemplate_IfExists_SkipsWhenAbsent()
    {
        var result = await DispatchAsync( $"DROP TEMPLATE {_templateName} IF EXISTS" );
        Assert.AreEqual( StatementOutcome.Skipped, result.Outcome );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DropComponent_IfExists_SkipsWhenAbsent()
    {
        var result = await DispatchAsync( $"DROP COMPONENT {_componentName} IF EXISTS" );
        Assert.AreEqual( StatementOutcome.Skipped, result.Outcome );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DropComponent_RemovesComponent()
    {
        var body = JsonNode.Parse( """
            { "template": { "mappings": { "properties": { "@timestamp": { "type": "date" } } } } }
            """ );
        await DispatchAsync( $"CREATE COMPONENT {_componentName} WITH BODY $body", body );

        var result = await DispatchAsync( $"DROP COMPONENT {_componentName}" );
        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var head = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.HEAD, $"_component_template/{_componentName}", default );
        Assert.AreEqual( 404, head.HttpStatusCode );
    }

    // ---- CREATE / APPLY POLICY ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task CreatePolicy_PutsIsmPolicy()
    {
        var body = MinimalIsmPolicyBody();

        var result = await DispatchAsync( $"CREATE POLICY {_policyId} WITH BODY $body", body );
        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var get = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.GET, $"_plugins/_ism/policies/{_policyId}", default );
        Assert.AreEqual( 200, get.HttpStatusCode );
        StringAssert.Contains( get.Body!, _policyId );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task ApplyPolicy_ReportsAtLeastOneIndexUpdated()
    {
        // Pre-create the policy and a target index
        var policyBody = MinimalIsmPolicyBody();
        var createPolicy = await DispatchAsync(
            $"CREATE POLICY {_policyId} WITH BODY $body", policyBody );
        Assert.IsTrue( createPolicy.IsSuccess, $"create policy failed: {createPolicy.Detail}" );

        await DispatchAsync( $"CREATE INDEX {_indexName}" );

        // Apply the policy to the index pattern. The dispatcher inspects the
        // ISM add response body and fails the statement when `updated_indices`
        // is 0 or `failures` is true — so a passing IsSuccess here is the
        // contract assertion that the policy was bound to at least one
        // matching index. (ISM's `_ism/explain` endpoint reflects management
        // state asynchronously via a background job and isn't a deterministic
        // post-condition to assert in tests.)
        var result = await DispatchAsync( $"APPLY POLICY {_policyId} TO {_indexPattern}" );
        Assert.IsTrue( result.IsSuccess, $"apply policy failed: {result.Detail}" );
        StringAssert.Contains( result.Detail!, "1 indices",
            $"expected updated_indices >= 1; got: {result.Detail}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task CreatePolicy_TwiceWithMutatedBody_SecondCallSucceedsViaCasRetry()
    {
        // CREATE POLICY is idempotent — re-running with a mutated body
        // upserts via a CAS retry on _seq_no/_primary_term. Required for
        // the [Migration(N, journal: false)] reconciliation pattern (see
        // provider README "Three temporal scopes for ISM attachment");
        // a non-idempotent CREATE POLICY would fail with HTTP 409 on
        // every re-run after the first.
        var firstBody = MinimalIsmPolicyBody();
        var firstResult = await DispatchAsync(
            $"CREATE POLICY {_policyId} WITH BODY $body", firstBody );
        Assert.IsTrue( firstResult.IsSuccess, $"first create failed: {firstResult.Detail}" );

        // Mutate the description so we can verify the retry actually
        // overwrote the policy rather than no-op'd. A semantically
        // meaningful change (different default_state, additional state)
        // would also work; description is enough for the post-condition
        // and keeps the policy minimal.
        var mutatedBody = JsonNode.Parse( """
            {
              "policy": {
                "description": "test policy (mutated)",
                "default_state": "hot",
                "states": [
                  {
                    "name": "hot",
                    "actions": [],
                    "transitions": []
                  }
                ]
              }
            }
            """ );
        var secondResult = await DispatchAsync(
            $"CREATE POLICY {_policyId} WITH BODY $body", mutatedBody );

        Assert.IsTrue( secondResult.IsSuccess,
            $"second create (CAS retry path) failed: {secondResult.Detail}" );
        StringAssert.Contains( secondResult.Detail!, "CAS retry",
            $"expected detail to indicate CAS retry path; got: {secondResult.Detail}" );

        // Post-condition: GET shows the mutated description, confirming
        // the retry actually overwrote rather than silently succeeded.
        var ll = OpenSearchTestContainer.LowLevelClient;
        var get = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.GET, $"_plugins/_ism/policies/{_policyId}", default );
        Assert.AreEqual( 200, get.HttpStatusCode );
        StringAssert.Contains( get.Body!, "test policy (mutated)",
            $"expected mutated description in policy body after CAS retry; got: {get.Body}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task ApplyPolicy_NoMatchingIndices_Fails()
    {
        // R-30 contract: ISM's add returns HTTP 200 even when zero indices
        // matched — the dispatcher must surface that as a Failed outcome so
        // authors don't get a false-positive migration record.
        var policyBody = MinimalIsmPolicyBody();
        await DispatchAsync( $"CREATE POLICY {_policyId} WITH BODY $body", policyBody );

        // No index created — pattern matches nothing.
        var result = await DispatchAsync( $"APPLY POLICY {_policyId} TO {_indexPattern}" );

        Assert.IsFalse( result.IsSuccess,
            $"expected Failed outcome on zero-match apply; got {result.Outcome}: {result.Detail}" );
    }

    // ---- DROP POLICY ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DropPolicy_RemovesPolicy()
    {
        // Pre-create the policy.
        var policyBody = MinimalIsmPolicyBody();
        var create = await DispatchAsync(
            $"CREATE POLICY {_policyId} WITH BODY $body", policyBody );
        Assert.IsTrue( create.IsSuccess, $"create policy failed: {create.Detail}" );

        // Drop it.
        var drop = await DispatchAsync( $"DROP POLICY {_policyId}" );
        Assert.IsTrue( drop.IsSuccess, $"drop policy failed: {drop.Detail}" );

        // Post-condition: GET returns 404.
        var ll = OpenSearchTestContainer.LowLevelClient;
        var get = await ll.DoRequestAsync<StringResponse>(
            OpenSearch.Net.HttpMethod.GET, $"_plugins/_ism/policies/{_policyId}", default );
        Assert.AreNotEqual( 200, get.HttpStatusCode,
            $"expected non-200 after DROP POLICY; got {get.HttpStatusCode}, body={get.Body}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DropPolicy_IfExists_MissingPolicy_Skips()
    {
        // No prior CREATE - the IF EXISTS guard should turn this into a Skipped
        // outcome rather than a Failed one.
        var result = await DispatchAsync( $"DROP POLICY {_policyId} IF EXISTS" );
        Assert.AreEqual( StatementOutcome.Skipped, result.Outcome,
            $"expected Skipped on missing policy under IF EXISTS; got {result.Outcome}: {result.Detail}" );
    }

    // ---- DETACH POLICY ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DetachPolicy_FollowedByDropPolicy_Succeeds()
    {
        // The cluster rejects DROP POLICY while any index still references the
        // policy. The canonical lifecycle is APPLY -> DETACH -> DROP; this
        // exercises that flow end-to-end.
        var policyBody = MinimalIsmPolicyBody();
        await DispatchAsync(
            $"CREATE POLICY {_policyId} WITH BODY $body", policyBody );
        await DispatchAsync( $"CREATE INDEX {_indexName}" );

        var apply = await DispatchAsync( $"APPLY POLICY {_policyId} TO {_indexPattern}" );
        Assert.IsTrue( apply.IsSuccess, $"apply failed: {apply.Detail}" );

        var detach = await DispatchAsync( $"DETACH POLICY FROM INDEX {_indexPattern}" );
        Assert.IsTrue( detach.IsSuccess, $"detach failed: {detach.Detail}" );

        var drop = await DispatchAsync( $"DROP POLICY {_policyId}" );
        Assert.IsTrue( drop.IsSuccess, $"drop after detach failed: {drop.Detail}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase2" )]
    public async Task DetachPolicy_NoMatchingIndices_IsIdempotentNoOp()
    {
        // ISM remove returns HTTP 200 even when zero indices match - that's
        // the cluster's idempotent contract. The dispatcher must surface this
        // as Executed (informational), NOT Failed, because operators routinely
        // detach from patterns that may have already been cleaned up.
        var result = await DispatchAsync( $"DETACH POLICY FROM INDEX {_indexPattern}" );
        Assert.IsTrue( result.IsSuccess,
            $"expected Executed outcome on zero-match detach; got {result.Outcome}: {result.Detail}" );
        StringAssert.Contains( result.Detail!, "0 indices" );
    }

    private static JsonNode MinimalIsmPolicyBody() => JsonNode.Parse( """
        {
          "policy": {
            "description": "test policy",
            "default_state": "hot",
            "states": [
              {
                "name": "hot",
                "actions": [],
                "transitions": []
              }
            ]
          }
        }
        """ )!;
}
#endif
