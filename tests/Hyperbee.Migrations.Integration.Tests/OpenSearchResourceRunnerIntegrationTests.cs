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
// Phase 1 Slice C.2 — End-to-end resource runner integration test.
//
// Validates that a multi-statement migration written as a JSON wrapper
// (statements[] array with sibling $body refs) runs correctly through
// parser -> middleware -> dispatcher -> cluster.
//
// We use the runner's `RunStatementsFromJsonAsync` test-friendly path
// to avoid wiring an embedded resource into the integration test
// project. The embedded-resource path (StatementsFromAsync) is
// exercised via the samples project in Phase 3.

[TestClass]
// Gating (ADR-0031): shared assembly-fixture container, no Docker image build,
// not multi-node. Runs on every PR.
[TestCategory( "Gating" )]
public class OpenSearchResourceRunnerIntegrationTests
{
    private OpenSearchResourceRunner<DummyMigration> _runner = null!;
    private string _indexName = null!;

    // Stub migration class to satisfy the generic constraint. Not actually
    // discovered or run — just used for resource-path resolution in the
    // public StatementsFromAsync path (which we don't exercise here).
    public sealed class DummyMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    // Minimal stand-in for IMigrationRecordStore — the resource runner only
    // touches it on the rollback path, which these tests don't exercise.
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

        _indexName = $"runner-test-{Guid.NewGuid():n}";
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await OpenSearchTestContainer.LowLevelClient.Indices.DeleteAsync<StringResponse>( _indexName );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task RunStatementsFromJsonAsync_MultiStatementMigration_ExecutesAllInOrder()
    {
        var json = $$"""
            {
              "statements": [
                {
                  "statement": "CREATE INDEX {{_indexName}} WITH BODY $usersIndex",
                  "usersIndex": {
                    "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
                    "mappings": {
                      "properties": {
                        "id":    { "type": "keyword" },
                        "email": { "type": "keyword" }
                      }
                    }
                  }
                },
                { "statement": "REFRESH {{_indexName}}" },
                { "statement": "WAIT FOR YELLOW ON {{_indexName}} TIMEOUT 30s" }
              ]
            }
            """;

        await _runner.RunStatementsFromJsonAsync( json );

        // Verify the index was created
        var existsResp = await OpenSearchTestContainer.LowLevelClient.Indices.ExistsAsync<StringResponse>( _indexName );
        Assert.AreEqual( 200, existsResp.HttpStatusCode );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task RunStatementsFromJsonAsync_SafeDefaultsAppliedAcrossPipeline_DynamicStrictInjected()
    {
        // CREATE INDEX with a body that doesn't set `dynamic`. The dispatcher's
        // safe-default merge should inject `dynamic: strict`. Verify by
        // attempting to index a doc with an undeclared field — cluster rejects.
        var json = $$"""
            {
              "statements": [
                {
                  "statement": "CREATE INDEX {{_indexName}} WITH BODY $body",
                  "body": {
                    "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
                    "mappings": {
                      "properties": { "id": { "type": "keyword" } }
                    }
                  }
                }
              ]
            }
            """;

        await _runner.RunStatementsFromJsonAsync( json );

        // Now attempt to index a doc with an undeclared field; cluster should reject.
        // The harness uses ThrowExceptions(), so a non-2xx surfaces as an exception.
        var ll = OpenSearchTestContainer.LowLevelClient;
        try
        {
            await ll.IndexAsync<StringResponse>( _indexName, "1",
                PostData.String( """{ "id": "1", "undeclared": "value" }""" ) );
            Assert.Fail( "Expected the cluster to reject the undeclared field due to dynamic:strict." );
        }
        catch ( OpenSearchClientException ex ) when ( ex.Response?.HttpStatusCode == 400 )
        {
            StringAssert.Contains( ex.Message, "strict_dynamic_mapping",
                "Safe-default dynamic:strict should have caused the strict_dynamic_mapping rejection." );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task RunStatementsFromJsonAsync_FailedStatement_ThrowsMigrationExceptionWithContext()
    {
        // Send an UPDATE MAPPING with no $body — the dispatcher's UPDATE MAPPING
        // handler returns a Failed StatementResult, which the runner wraps in
        // MigrationException. The exception message should include the statement
        // index and verb so authors can identify which statement failed.
        var json = $$"""
            {
              "statements": [
                { "statement": "UPDATE MAPPING ON nonexistent" }
              ]
            }
            """;

        var ex = await Assert.ThrowsExactlyAsync<MigrationException>(
            async () => await _runner.RunStatementsFromJsonAsync( json ) );

        StringAssert.Contains( ex.Message, "UPDATE MAPPING" );
        StringAssert.Contains( ex.Message, "Statement 0" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task RunStatementsFromJsonAsync_MissingBodyRef_ThrowsClearError()
    {
        // The statement says WITH BODY $myBody but no sibling property exists.
        var json = $$"""
            {
              "statements": [
                { "statement": "CREATE INDEX {{_indexName}} WITH BODY $myBody" }
              ]
            }
            """;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await _runner.RunStatementsFromJsonAsync( json ) );

        StringAssert.Contains( ex.Message, "myBody" );
        StringAssert.Contains( ex.Message, "sibling property" );
    }
}
#endif
