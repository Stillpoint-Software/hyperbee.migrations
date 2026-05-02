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
    private readonly IMigrationRecordStore _recordStore;

    public OpenSearchResourceRunner(
        IOpenSearchClient client,
        OpenSearchMigrationOptions options,
        StatementDispatcher dispatcher,
        OpenSearchStatementParser parser,
        TimeProvider timeProvider,
        ILogger<TMigration> logger,
        IMigrationRecordStore recordStore )
    {
        _client = client;
        _options = options;
        _dispatcher = dispatcher;
        _parser = parser;
        _timeProvider = timeProvider;
        _logger = logger;
        _recordStore = recordStore;
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

    // R-19 — Down direction. Each statement entry in the JSON may carry an
    // optional `rollback` property whose value is itself a statement string.
    // We dispatch those rollback statements in REVERSE declaration order
    // (LIFO — the last operation applied is the first to undo). A failure
    // halts the sequence and writes the migration's ledger entry to
    // `partially_rolled_back` with the failing-statement index, so subsequent
    // runs are refused unless ForceResume is set.
    //
    // Body refs in rollback statements resolve against sibling properties of
    // the SAME statement object (the one that declared the rollback), per
    // ADR-0002 / R-09 — symmetric with the up path. Most rollbacks are
    // simple (DROP INDEX, ALIAS SWAP back) and don't need a body.

    public Task RollbackStatementsFromAsync( TMigration migration, string resourceName, CancellationToken cancellationToken = default )
        => RollbackStatementsFromAsync( migration, new[] { resourceName }, default, cancellationToken );

    public Task RollbackStatementsFromAsync( TMigration migration, string resourceName, TimeSpan? timeout, CancellationToken cancellationToken = default )
        => RollbackStatementsFromAsync( migration, new[] { resourceName }, timeout, cancellationToken );

    public Task RollbackStatementsFromAsync( TMigration migration, string[] resourceNames, CancellationToken cancellationToken = default )
        => RollbackStatementsFromAsync( migration, resourceNames, default, cancellationToken );

    public async Task RollbackStatementsFromAsync( TMigration migration, string[] resourceNames, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        ArgumentNullException.ThrowIfNull( migration );
        ThrowIfNoResourceLocationFor();

        var migrationName = Migration.VersionedName<TMigration>();
        var recordId = _options.Conventions.GetRecordId( migration );

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        // Roll back resources in REVERSE order; within each resource, also
        // reverse the statement order. A migration that pulls multiple
        // resources in Up order [a, b, c] is undone as [c-reversed, b-reversed,
        // a-reversed] so the cluster state retraces the path it came in on.
        for ( var ri = resourceNames.Length - 1; ri >= 0; ri-- )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            var json = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceNames[ri]}" );
            await RollbackStatementsFromJsonAsync( json, recordId, operationCancelToken ).ConfigureAwait( false );
        }
    }

    /// <summary>
    /// Public for integration tests and for callers that build resource bodies
    /// programmatically. Mirrors RunStatementsFromJsonAsync but dispatches the
    /// `rollback` field of each entry in REVERSE order.
    /// </summary>
    public async Task RollbackStatementsFromJsonAsync( string json, string recordId, CancellationToken cancellationToken = default )
    {
        var root = JsonNode.Parse( json )
            ?? throw new InvalidOperationException( "Statements JSON is empty or invalid." );

        var statements = root["statements"]?.AsArray()
            ?? throw new InvalidOperationException( "Statements JSON missing required `statements` array." );

        // First pass: validate that every statement has a rollback. R-19 is
        // explicit: missing-rollback is an author-time decision; running half
        // the rollback set then discovering a missing rollback would leave
        // the cluster in a half-rolled-back state. Validate up front so we
        // refuse Down loudly before mutating anything.
        for ( var i = 0; i < statements.Count; i++ )
        {
            var entry = statements[i] as JsonObject
                ?? throw new InvalidOperationException( $"statements[{i}] is not a JSON object." );

            if ( entry["rollback"] is null )
            {
                throw new RollbackNotSupportedException( i,
                    $"statements[{i}] has no `rollback` field. Down direction is opt-in per statement (R-19). " +
                    $"Add a `rollback` statement string, or document the migration as irreversible and remove it from the Down path." );
            }
        }

        // Second pass: dispatch rollbacks in reverse order. On the first
        // failure, write a `partially_rolled_back` ledger entry with the
        // index of the failing statement and rethrow.
        for ( var i = statements.Count - 1; i >= 0; i-- )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = (JsonObject) statements[i]!;
            var rollbackText = entry["rollback"]!.GetValue<string>();

            var ast = _parser.Parse( rollbackText );

            JsonNode? resolvedBody = null;
            var bodyRefName = ExtractBodyRefName( ast );
            if ( bodyRefName is not null )
            {
                var sibling = entry[bodyRefName]
                    ?? throw new InvalidOperationException(
                        $"statements[{i}] rollback: `WITH BODY ${bodyRefName}` references a sibling property that does not exist." );

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

            _logger.LogInformation( "Rollback dispatch (reverse) {idx}: {verb}", i, ast.Verb );

            StatementResult result;
            try
            {
                result = await _dispatcher.DispatchAsync( ast, context ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                await WritePartialRollbackIfAvailableAsync( recordId, i, ex.Message ).ConfigureAwait( false );
                throw new MigrationException(
                    $"Rollback statement {i} ({ast.Verb}) threw: {ex.Message}. " +
                    $"Ledger marked `partially_rolled_back` at index {i}; subsequent runs require ForceResume.",
                    ex );
            }

            if ( !result.IsSuccess )
            {
                var reason = result.Detail ?? "unknown failure";
                await WritePartialRollbackIfAvailableAsync( recordId, i, reason ).ConfigureAwait( false );

                throw new MigrationException(
                    $"Rollback statement {i} ({ast.Verb}) failed: {reason}. " +
                    $"Ledger marked `partially_rolled_back` at index {i}; subsequent runs require ForceResume.",
                    result.Exception ?? new InvalidOperationException( reason ) );
            }

            _logger.LogInformation(
                "Rollback statement {idx} {outcome}: {detail}",
                i, result.Outcome, result.Detail ?? "(no detail)" );
        }
    }

    private async Task WritePartialRollbackIfAvailableAsync( string recordId, int failedStatementIndex, string error )
    {
        // The IMigrationRecordStore contract is provider-agnostic; the rich
        // partial-rollback write is OpenSearch-specific. Cast to the concrete
        // type when we own it (we always do under the standard DI registration).
        if ( _recordStore is OpenSearchRecordStore os )
        {
            try
            {
                await os.WritePartialRollbackAsync( recordId, failedStatementIndex, error ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                // Don't mask the original rollback failure with a ledger-write
                // failure — log it loudly. The operator now has TWO problems
                // to investigate, but obscuring either makes diagnosis harder.
                _logger.LogError( ex,
                    "Partial-rollback ledger write for `{recordId}` failed AFTER rollback statement {idx} failed. " +
                    "Cluster state may be inconsistent AND the ledger was not updated. Manual reconciliation required.",
                    recordId, failedStatementIndex );
            }
        }
        else
        {
            _logger.LogWarning(
                "Partial-rollback semantics require OpenSearchRecordStore; the registered IMigrationRecordStore " +
                "is `{type}`. Ledger NOT updated to partially_rolled_back. (R-19 lockout will not fire on subsequent runs.)",
                _recordStore.GetType().FullName );
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
