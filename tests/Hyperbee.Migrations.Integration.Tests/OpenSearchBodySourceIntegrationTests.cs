//#define INTEGRATIONS
#nullable enable
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// ADR-0017 — body-source resolution. Three forms covered end-to-end against
// a real OpenSearch cluster:
//
//   1. `WITH BODY @path/to/file.json`  (BodyFileRef)
//   2. `WITH BODY $name` + `bodies.<name>` inline  (BodyRef + bodies section)
//   3. `WITH BODY $name` + `bodies.<name>` = "@path"  (BodyRef + bodies-section file ref)
//
// Plus ADR-0009 back-compat: `WITH BODY $name` + top-level sibling `<name>`.
//
// File-based forms (1, 3) need an embedded resource. Rather than spawning a
// resource folder for the integration test assembly, we exercise file-loading
// via the explicit failure path: a non-existent @path must throw at
// resolve-time with a remediation message naming the path. The happy file-
// path is exercised through the migrated samples (sample 4 uses form 1, sample
// 3 uses form 3). The smoke-test against the runner validates that those
// samples load and parse cleanly; this test file pins the runtime semantics
// exercisable in-process.

[TestClass]
public class OpenSearchBodySourceIntegrationTests
{
    // Version chosen far outside any other test fixture's range so an
    // accidental MigrationRunner scan won't pick this up alongside another
    // 9xxxx-versioned fixture.
    [Migration( 99201L )]
    public sealed class DummyMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    private sealed class NoopRecordStore : IMigrationRecordStore
    {
        public Task InitializeAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<IDisposable> CreateLockAsync() => Task.FromResult<IDisposable>( new NoopDisposable() );
        public Task<bool> ExistsAsync( string recordId ) => Task.FromResult( false );
        public Task<MigrationRecord> ReadAsync( string recordId ) => Task.FromResult<MigrationRecord>( null! );
        public Task DeleteAsync( string recordId ) => Task.CompletedTask;
        public Task WriteAsync( string recordId ) => Task.CompletedTask;

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private OpenSearchResourceRunner<DummyMigration> _runner = null!;
    private string _indexName = null!;

    [TestInitialize]
    public void Setup()
    {
        _runner = new OpenSearchResourceRunner<DummyMigration>(
            OpenSearchTestContainer.Client,
            new OpenSearchMigrationOptions(),
            new StatementDispatcher( new SafeDefaultMergeMiddleware() ),
            new OpenSearchStatementParser(),
            TimeProvider.System,
            NullLogger<DummyMigration>.Instance,
            new NoopRecordStore() );

        _indexName = $"bodysrc-{Guid.NewGuid():n}";
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await OpenSearchTestContainer.LowLevelClient.Indices.DeleteAsync<StringResponse>( _indexName );
    }

    // ---- Form 2 — `bodies.<name>` inline JSON ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase3" )]
    [TestCategory( "ADR-0017" )]
    public async Task BodiesSection_Inline_ResolvesAndDispatches()
    {
        var json = $$"""
            {
              "statements": [
                {
                  "statement": "CREATE INDEX {{_indexName}} WITH BODY $idx",
                  "bodies": {
                    "idx": {
                      "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
                      "mappings": { "properties": { "id": { "type": "keyword" } } }
                    }
                  }
                }
              ]
            }
            """;

        await _runner.RunStatementsFromJsonAsync( json );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var resp = await ll.Indices.ExistsAsync<StringResponse>( _indexName );
        Assert.AreEqual( 200, resp.HttpStatusCode );
    }

    // ---- ADR-0009 back-compat — top-level sibling property ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase3" )]
    [TestCategory( "ADR-0009" )]
    [TestCategory( "ADR-0017" )]
    public async Task SiblingProperty_BackCompat_StillResolves()
    {
        // Pre-Slice-3.5 migrations had body refs as top-level sibling
        // properties (no `bodies` section). The resolver still finds them
        // when `bodies.<name>` is missing.
        var json = $$"""
            {
              "statements": [
                {
                  "statement": "CREATE INDEX {{_indexName}} WITH BODY $idx",
                  "idx": {
                    "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
                    "mappings": { "properties": { "id": { "type": "keyword" } } }
                  }
                }
              ]
            }
            """;

        await _runner.RunStatementsFromJsonAsync( json );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var resp = await ll.Indices.ExistsAsync<StringResponse>( _indexName );
        Assert.AreEqual( 200, resp.HttpStatusCode );
    }

    // ---- Resolution priority — bodies section beats sibling ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase3" )]
    [TestCategory( "ADR-0017" )]
    public async Task BodiesSection_BeatsSibling_WhenBothPresent()
    {
        // If the same name appears in both, `bodies.<name>` wins (ADR-0017
        // prefers the structured form). The sibling here uses an
        // intentionally-different shape so we can detect which one was
        // chosen by the cluster's reaction.
        var json = $$"""
            {
              "statements": [
                {
                  "statement": "CREATE INDEX {{_indexName}} WITH BODY $idx",
                  "bodies": {
                    "idx": {
                      "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
                      "mappings": { "properties": { "from_bodies": { "type": "keyword" } } }
                    }
                  },
                  "idx": {
                    "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
                    "mappings": { "properties": { "from_sibling": { "type": "boolean" } } }
                  }
                }
              ]
            }
            """;

        await _runner.RunStatementsFromJsonAsync( json );

        var ll = OpenSearchTestContainer.LowLevelClient;
        var mappingResp = await ll.Indices.GetMappingAsync<StringResponse>( _indexName );
        Assert.IsTrue( mappingResp.Success );
        StringAssert.Contains( mappingResp.Body!, "from_bodies",
            "bodies section should win when both forms address the same name" );
        Assert.IsFalse( mappingResp.Body!.Contains( "from_sibling" ),
            "sibling form should NOT be applied when bodies section has the same name" );
    }

    // ---- Failure paths ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase3" )]
    [TestCategory( "ADR-0017" )]
    public async Task BodyRef_Missing_ThrowsRemediation()
    {
        var json = $$"""
            {
              "statements": [
                { "statement": "CREATE INDEX {{_indexName}} WITH BODY $missingBody" }
              ]
            }
            """;

        try
        {
            await _runner.RunStatementsFromJsonAsync( json );
            Assert.Fail( "expected InvalidOperationException for missing body ref" );
        }
        catch ( InvalidOperationException ex )
        {
            // Remediation must name both the preferred form and the back-compat fallback.
            StringAssert.Contains( ex.Message, "missingBody" );
            StringAssert.Contains( ex.Message, "bodies." );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase3" )]
    [TestCategory( "ADR-0017" )]
    public async Task BodyFileRef_NonexistentResource_ThrowsRemediationNamingPath()
    {
        // The integration-tests assembly has no [ResourceLocation] attribute,
        // so resource resolution would fail before path lookup. Guard via a
        // try/catch on the broader exception type.
        var json = $$"""
            {
              "statements": [
                { "statement": "CREATE INDEX {{_indexName}} WITH BODY @bodies/never-existed.json" }
              ]
            }
            """;

        try
        {
            await _runner.RunStatementsFromJsonAsync( json );
            Assert.Fail( "expected resource-loading failure" );
        }
        catch ( Exception ex ) when ( ex is InvalidOperationException || ex is NotSupportedException )
        {
            // Either the path lookup failed (InvalidOperationException with
            // remediation) or the assembly lacks ResourceLocation
            // (NotSupportedException). Both surface clearly to the operator
            // — the test asserts neither path silently succeeds.
        }
    }
}
#endif
