#nullable enable
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Resources;
using Hyperbee.Migrations.Wait;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.Resources;

// Resource runner per ADR-0002. Loads embedded `statements.json` files from
// the migration's assembly, parses each statement via Parlot, resolves
// $body sibling references, and dispatches via StatementDispatcher.
//
// JSON shape (per ADR-0002):
//   {
//     "statements": [
//       { "statement": "CREATE INDEX users WITH BODY $usersIndex",
//         "usersIndex": { "settings": {...}, "mappings": {...} } },
//       { "statement": "REFRESH users" }
//     ]
//   }
//
// Sibling JSON properties on the same statement object are resolved as
// body references. The middleware (SafeDefaultMergeMiddleware) merges
// safe-default flags into the resolved body before dispatch (per
// ADR-0011 hybrid + ADR-0015 offline-pure parser).

public class OpenSearchResourceRunner<TMigration> where TMigration : Migration
{
    private readonly IOpenSearchClient _client;
    private readonly OpenSearchMigrationOptions _options;
    private readonly StatementDispatcher _dispatcher;
    private readonly OpenSearchStatementParser _parser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public OpenSearchResourceRunner(
        IOpenSearchClient client,
        OpenSearchMigrationOptions options,
        StatementDispatcher dispatcher,
        OpenSearchStatementParser parser,
        TimeProvider timeProvider,
        ILogger<TMigration> logger )
    {
        _client = client;
        _options = options;
        _dispatcher = dispatcher;
        _parser = parser;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StatementsFromAsync( string resourceName, CancellationToken cancellationToken = default )
        => StatementsFromAsync( new[] { resourceName }, default, cancellationToken );

    public Task StatementsFromAsync( string resourceName, TimeSpan? timeout, CancellationToken cancellationToken = default )
        => StatementsFromAsync( new[] { resourceName }, timeout, cancellationToken );

    public Task StatementsFromAsync( string[] resourceNames, CancellationToken cancellationToken = default )
        => StatementsFromAsync( resourceNames, default, cancellationToken );

    public async Task StatementsFromAsync( string[] resourceNames, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        ThrowIfNoResourceLocationFor();

        var migrationName = Migration.VersionedName<TMigration>();

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        foreach ( var resourceName in resourceNames )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            var json = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceName}" );

            await RunStatementsFromJsonAsync( json, operationCancelToken ).ConfigureAwait( false );
        }
    }

    /// <summary>
    /// Parses and dispatches statements from a JSON string. Public for
    /// integration tests and for callers that build resource bodies
    /// programmatically; embedded-resource consumers go through
    /// StatementsFromAsync.
    /// </summary>
    public async Task RunStatementsFromJsonAsync( string json, CancellationToken cancellationToken = default )
    {
        var root = JsonNode.Parse( json )
            ?? throw new InvalidOperationException( "Statements JSON is empty or invalid." );

        var statements = root["statements"]?.AsArray()
            ?? throw new InvalidOperationException( "Statements JSON missing required `statements` array." );

        for ( var i = 0; i < statements.Count; i++ )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = statements[i] as JsonObject
                ?? throw new InvalidOperationException( $"statements[{i}] is not a JSON object." );

            var statementText = entry["statement"]?.GetValue<string>()
                ?? throw new InvalidOperationException( $"statements[{i}] missing `statement` field." );

            var ast = _parser.Parse( statementText );

            // Resolve $body sibling reference if present. Per ADR-0009 / R-09, $body
            // references resolve against sibling properties on the same statement
            // object. The reference name comes from the AST (e.g., CreateIndexAst.Body).

            JsonNode? resolvedBody = null;
            var bodyRefName = ExtractBodyRefName( ast );

            if ( bodyRefName is not null )
            {
                var sibling = entry[bodyRefName]
                    ?? throw new InvalidOperationException(
                        $"statements[{i}]: `WITH BODY ${bodyRefName}` references a sibling property that does not exist." );

                // Deep-clone via round-trip so the dispatcher's middleware can mutate
                // freely without affecting the parsed JSON tree.
                resolvedBody = JsonNode.Parse( sibling.ToJsonString() );
            }

            var context = new StatementContext
            {
                Client = _client,
                Options = _options,
                TimeProvider = _timeProvider,
                Logger = _logger,
                ResolvedBody = resolvedBody,
                CancellationToken = cancellationToken
            };

            _logger.LogInformation( "Dispatching statement {idx}: {verb}", i, ast.Verb );

            var result = await _dispatcher.DispatchAsync( ast, context ).ConfigureAwait( false );

            if ( !result.IsSuccess )
            {
                throw new MigrationException(
                    $"Statement {i} ({ast.Verb}) failed: {result.Detail}",
                    result.Exception ?? new InvalidOperationException( result.Detail ?? "unknown failure" ) );
            }

            _logger.LogInformation(
                "Statement {idx} {outcome}: {detail}",
                i, result.Outcome, result.Detail ?? "(no detail)" );
        }
    }

    private static string? ExtractBodyRefName( Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.StatementAst ast )
    {
        // Cast through the known body-bearing AST shapes. Each verb that supports
        // WITH BODY $name carries the BodyRef on its record type.
        return ast switch
        {
            Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.CreateIndexAst c => c.Body?.Name,
            Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.ReindexAst r => r.Body?.Name,
            Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.UpdateMappingAst um => um.Body?.Name,
            Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.UpdateSettingsAst us => us.Body?.Name,
            Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.CreateTemplateAst ct => ct.Body?.Name,
            Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.CreateComponentAst cc => cc.Body?.Name,
            Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast.CreatePolicyAst cp => cp.Body?.Name,
            _ => null
        };
    }

    private static void ThrowIfNoResourceLocationFor()
    {
        var exists = typeof( TMigration )
            .Assembly
            .GetCustomAttributes( typeof( ResourceLocationAttribute ), false )
            .Cast<ResourceLocationAttribute>()
            .Any();

        if ( !exists )
            throw new NotSupportedException( $"Missing required assembly attribute: {nameof( ResourceLocationAttribute )}." );
    }
}
