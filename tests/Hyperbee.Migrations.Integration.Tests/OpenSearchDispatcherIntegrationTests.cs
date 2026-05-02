//#define INTEGRATIONS
#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Hyperbee.Migrations.Providers.OpenSearch;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Phase 1 Slice C.1 — StatementDispatcher integration tests against real
// OpenSearch. One test per verb to validate the AST -> middleware -> compile
// -> dispatch -> cluster path end-to-end. Body resolution (the resource
// runner's job per Slice C.2) is done inline in the tests via JsonNode.

[TestClass]
public class OpenSearchDispatcherIntegrationTests
{
    private OpenSearchStatementParser _parser = null!;
    private StatementDispatcher _dispatcher = null!;
    private OpenSearchMigrationOptions _options = null!;

    [TestInitialize]
    public void Setup()
    {
        _parser = new OpenSearchStatementParser();
        _dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        _options = new OpenSearchMigrationOptions();
    }

    private StatementContext MakeContext( JsonNode? resolvedBody = null )
        => new()
        {
            Client = OpenSearchTestContainer.Client,
            Options = _options,
            TimeProvider = TimeProvider.System,
            Logger = NullLogger.Instance,
            ResolvedBody = resolvedBody,
            CancellationToken = default
        };

    private static string MakeIndexName( string baseName )
        => $"{baseName}-{Guid.NewGuid():N}".ToLowerInvariant();

    private static async Task DeleteIfExistsAsync( string idx )
        => await OpenSearchTestContainer.LowLevelClient.Indices.DeleteAsync<StringResponse>( idx );

    // --- CREATE INDEX ---

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task CreateIndex_Dispatch_CreatesIndexOnCluster()
    {
        var name = MakeIndexName( "users" );
        var ast = _parser.Parse( $"CREATE INDEX {name}" );
        var ctx = MakeContext();

        try
        {
            var result = await _dispatcher.DispatchAsync( ast, ctx );
            Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );
            Assert.AreEqual( StatementOutcome.Executed, result.Outcome );

            var existsResp = await OpenSearchTestContainer.LowLevelClient.Indices.ExistsAsync<StringResponse>( name );
            Assert.AreEqual( 200, existsResp.HttpStatusCode );
        }
        finally
        {
            await DeleteIfExistsAsync( name );
        }
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task CreateIndex_IfNotExistsGuard_SkipsWhenAlreadyPresent()
    {
        var name = MakeIndexName( "users" );

        try
        {
            // Create once
            await _dispatcher.DispatchAsync( _parser.Parse( $"CREATE INDEX {name}" ), MakeContext() );

            // Second create with IF NOT EXISTS should skip
            var ast = _parser.Parse( $"CREATE INDEX {name} IF NOT EXISTS" );
            var result = await _dispatcher.DispatchAsync( ast, MakeContext() );

            Assert.AreEqual( StatementOutcome.Skipped, result.Outcome );
            StringAssert.Contains( result.Detail!, "IF NOT EXISTS" );
        }
        finally
        {
            await DeleteIfExistsAsync( name );
        }
    }

    // --- DROP INDEX ---

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task DropIndex_Dispatch_DeletesIndexOnCluster()
    {
        var name = MakeIndexName( "users" );

        await _dispatcher.DispatchAsync( _parser.Parse( $"CREATE INDEX {name}" ), MakeContext() );

        var ast = _parser.Parse( $"DROP INDEX {name}" );
        var result = await _dispatcher.DispatchAsync( ast, MakeContext() );

        Assert.IsTrue( result.IsSuccess );
        var existsResp = await OpenSearchTestContainer.LowLevelClient.Indices.ExistsAsync<StringResponse>( name );
        Assert.AreEqual( 404, existsResp.HttpStatusCode );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task DropIndex_IfExistsGuard_SkipsWhenAbsent()
    {
        var name = MakeIndexName( "nonexistent" );

        var ast = _parser.Parse( $"DROP INDEX {name} IF EXISTS" );
        var result = await _dispatcher.DispatchAsync( ast, MakeContext() );

        Assert.AreEqual( StatementOutcome.Skipped, result.Outcome );
    }

    // --- UPDATE MAPPING ---

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task UpdateMapping_Dispatch_AddsFieldOnCluster()
    {
        var name = MakeIndexName( "users" );

        var initialBody = JsonNode.Parse( """
            {
              "mappings": { "properties": { "id": { "type": "keyword" } } }
            }
            """ );
        await _dispatcher.DispatchAsync(
            _parser.Parse( $"CREATE INDEX {name} WITH BODY $body" ),
            MakeContext( initialBody ) );

        try
        {
            var newProps = JsonNode.Parse( """
                { "properties": { "email": { "type": "keyword" } } }
                """ );

            var ast = _parser.Parse( $"UPDATE MAPPING ON {name} WITH BODY $newProps" );
            var result = await _dispatcher.DispatchAsync( ast, MakeContext( newProps ) );

            Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

            var mappingResp = await OpenSearchTestContainer.LowLevelClient.Indices.GetMappingAsync<StringResponse>( name );
            using var doc = JsonDocument.Parse( mappingResp.Body );
            var props = doc.RootElement
                .GetProperty( name )
                .GetProperty( "mappings" )
                .GetProperty( "properties" );
            Assert.IsTrue( props.TryGetProperty( "email", out _ ), "email field was not added by UPDATE MAPPING" );
        }
        finally
        {
            await DeleteIfExistsAsync( name );
        }
    }

    // --- UPDATE SETTINGS (dynamic) ---

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task UpdateSettings_DynamicSetting_NoCloseFlag_UpdatesOnCluster()
    {
        var name = MakeIndexName( "users" );

        await _dispatcher.DispatchAsync( _parser.Parse( $"CREATE INDEX {name}" ), MakeContext() );

        try
        {
            var newSettings = JsonNode.Parse( """
                { "index": { "refresh_interval": "5s" } }
                """ );

            var ast = _parser.Parse( $"UPDATE SETTINGS ON {name} WITH BODY $s" );
            var result = await _dispatcher.DispatchAsync( ast, MakeContext( newSettings ) );

            Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

            var getSettings = await OpenSearchTestContainer.LowLevelClient.Indices.GetSettingsAsync<StringResponse>( name );
            StringAssert.Contains( getSettings.Body, "refresh_interval", "refresh_interval was not updated" );
        }
        finally
        {
            await DeleteIfExistsAsync( name );
        }
    }

    // --- REFRESH ---

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task Refresh_Dispatch_Succeeds()
    {
        var name = MakeIndexName( "users" );
        await _dispatcher.DispatchAsync( _parser.Parse( $"CREATE INDEX {name}" ), MakeContext() );

        try
        {
            var ast = _parser.Parse( $"REFRESH {name}" );
            var result = await _dispatcher.DispatchAsync( ast, MakeContext() );

            Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );
        }
        finally
        {
            await DeleteIfExistsAsync( name );
        }
    }

    // --- WAIT FOR HEALTH ---

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task WaitForHealth_Yellow_Succeeds()
    {
        var ast = _parser.Parse( "WAIT FOR YELLOW TIMEOUT 30s" );
        var result = await _dispatcher.DispatchAsync( ast, MakeContext() );

        Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task WaitForHealth_OnSpecificIndex_Succeeds()
    {
        var name = MakeIndexName( "users" );
        await _dispatcher.DispatchAsync( _parser.Parse( $"CREATE INDEX {name}" ), MakeContext() );

        try
        {
            var ast = _parser.Parse( $"WAIT FOR YELLOW ON {name} TIMEOUT 30s" );
            var result = await _dispatcher.DispatchAsync( ast, MakeContext() );

            Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );
        }
        finally
        {
            await DeleteIfExistsAsync( name );
        }
    }

    // --- REINDEX ---

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Phase1" )]
    public async Task Reindex_BareDispatch_CopiesDocsWithOpTypeCreate()
    {
        var src = MakeIndexName( "users-v1" );
        var dst = MakeIndexName( "users-v2" );

        var ll = OpenSearchTestContainer.LowLevelClient;

        // Pre-declare schema so the dispatcher's dynamic:strict default permits the test docs
        var schemaBody = JsonNode.Parse( """
            {
              "mappings": {
                "properties": {
                  "id": { "type": "keyword" },
                  "v":  { "type": "keyword" }
                }
              }
            }
            """ );
        await _dispatcher.DispatchAsync(
            _parser.Parse( $"CREATE INDEX {src} WITH BODY $b" ), MakeContext( schemaBody ) );
        await _dispatcher.DispatchAsync(
            _parser.Parse( $"CREATE INDEX {dst} WITH BODY $b" ), MakeContext( JsonNode.Parse( schemaBody!.ToJsonString() ) ) );

        try
        {
            for ( var i = 1; i <= 3; i++ )
            {
                await ll.IndexAsync<StringResponse>( src, i.ToString(),
                    PostData.String( $$"""{ "id": "{{i}}", "v": "v1" }""" ),
                    new IndexRequestParameters { Refresh = Refresh.True } );
            }

            var ast = _parser.Parse( $"REINDEX FROM {src} TO {dst}" );
            var result = await _dispatcher.DispatchAsync( ast, MakeContext() );

            Assert.IsTrue( result.IsSuccess, $"dispatch failed: {result.Detail}" );

            await ll.Indices.RefreshAsync<StringResponse>( dst );
            var countResp = await ll.CountAsync<StringResponse>( dst, PostData.String( "{}" ) );
            using var doc = JsonDocument.Parse( countResp.Body );
            Assert.AreEqual( 3, doc.RootElement.GetProperty( "count" ).GetInt32() );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( $"{src},{dst}" );
        }
    }
}
#endif
