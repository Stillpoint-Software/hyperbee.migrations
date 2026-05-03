#nullable enable
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
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

        // R-15 — context filter at the resource-file level. Returns false
        // (with INFO log) when the file should be skipped; throws
        // MissingActiveContextException under RequireExplicit when the
        // operator hasn't supplied ActiveContext.
        if ( !ShouldRunForActiveContext( root ) )
            return;

        for ( var i = 0; i < statements.Count; i++ )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = statements[i] as JsonObject
                ?? throw new InvalidOperationException( $"statements[{i}] is not a JSON object." );

            var statementText = entry["statement"]?.GetValue<string>()
                ?? throw new InvalidOperationException( $"statements[{i}] missing `statement` field." );

            var ast = _parser.Parse( statementText );

            var resolvedBody = ResolveBody( ast, entry, statementIndex: i, contextLabel: null );

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

        // R-12 PerMigration — single consolidated cluster-health wait at the
        // end of the resource pass. No-op under PerStatement (the dirty set
        // stays empty because each statement waited inline) and Off (no
        // tracking happens). The flush context reuses the last statement's
        // context shape; only Client / Options / Logger / CT are read.
        await _dispatcher.FlushImplicitWaitsAsync( new StatementContext
        {
            Client = _client,
            Options = _options,
            TimeProvider = _timeProvider,
            Logger = _logger,
            ResolvedBody = null,
            CancellationToken = cancellationToken
        } ).ConfigureAwait( false );
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

        // R-15 — same context filter as the up path. Skipped files don't
        // touch the ledger either (no record was written; nothing to roll
        // back).
        if ( !ShouldRunForActiveContext( root ) )
            return;

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

            var resolvedBody = ResolveBody( ast, entry, statementIndex: i, contextLabel: "rollback" );

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

        // R-12 PerMigration end-of-rollback flush — symmetric with the up
        // path. Rollback statements include CREATE / DROP / REINDEX /
        // ALIAS SWAP, all of which are mutating and contribute to the
        // dirty index set under PerMigration mode.
        await _dispatcher.FlushImplicitWaitsAsync( new StatementContext
        {
            Client = _client,
            Options = _options,
            TimeProvider = _timeProvider,
            Logger = _logger,
            ResolvedBody = null,
            CancellationToken = cancellationToken
        } ).ConfigureAwait( false );
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

    // Body resolution per ADR-0017. Three forms supported:
    //
    //   1. `WITH BODY @path/to/file.json`   (BodyFileRef)
    //         Loads an embedded resource at the given path relative to the
    //         migration's resource folder.
    //
    //   2. `WITH BODY $name` + `bodies.<name>`   (BodyRef + structured)
    //         Looks up the named body in the entry's `bodies` object. The
    //         value is either an inline JSON object OR a string starting
    //         with `@` (a file reference, which is then resolved as in form 1).
    //
    //   3. `WITH BODY $name` + sibling `<name>`   (BodyRef + ADR-0009 back-compat)
    //         When `bodies.<name>` is missing, fall back to a top-level
    //         sibling property named <name>. Preserves ADR-0009 / R-09
    //         semantics so existing migrations keep working.
    //
    // The lookup order (bodies first, sibling fallback) means new authors
    // discover the structured form first, but legacy resources need no edits.

    // R-15 — file-level context filter. Reads an optional top-level
    // `context: ["prod", "staging"]` array from the statements.json wrapper
    // and decides whether the runner should process the file under the
    // configured ContextResolutionPolicy.
    //
    //   No `context:` block in the file        -> always run (returns true)
    //   File has `context:` AND ActiveContext null:
    //       SkipIfUnset       -> skip (INFO log)            -> false
    //       RequireExplicit   -> throw MissingActiveContextException
    //   File has `context:` AND ActiveContext set:
    //       any tag in ActiveContext intersects the file's list -> run -> true
    //       no intersection                                      -> skip -> false
    //
    // ActiveContext is comma-separated (e.g., "prod,canary") so a single
    // runner deployment can claim membership in multiple contexts. Matching
    // is case-sensitive — context tags are identifiers, not free-form text.
    private bool ShouldRunForActiveContext( JsonNode root )
    {
        var contextNode = root["context"];
        if ( contextNode is null )
            return true;   // file has no context block — always runs

        var fileContexts = contextNode.AsArray()
            .Select( n => n!.GetValue<string>() )
            .Where( s => !string.IsNullOrWhiteSpace( s ) )
            .ToArray();

        if ( fileContexts.Length == 0 )
            return true;   // empty `context: []` is degenerate; treat as no filter

        var activeRaw = _options.ActiveContext;

        if ( string.IsNullOrWhiteSpace( activeRaw ) )
        {
            if ( _options.ContextResolutionPolicy == ContextResolutionPolicy.RequireExplicit )
            {
                throw new MissingActiveContextException(
                    "Resource file declares a `context:` block " +
                    $"({string.Join( ", ", fileContexts )}) but ContextResolutionPolicy = RequireExplicit " +
                    "and OpenSearchMigrationOptions.ActiveContext is not set. " +
                    "Set Migrations:ActiveContext in configuration to a comma-separated list of " +
                    "context tags (e.g., \"prod\" or \"prod,canary\"). " +
                    "RequireExplicit is the production default; silent prod-everywhere behavior is forbidden." );
            }

            // SkipIfUnset (SDK default) — skip the file with INFO so ops can
            // see the gate fired.
            _logger.LogInformation(
                "Resource file skipped: declares `context: [{contexts}]` but ActiveContext is unset (policy = SkipIfUnset).",
                string.Join( ", ", fileContexts ) );
            return false;
        }

        var activeTags = activeRaw
            .Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .ToHashSet( StringComparer.Ordinal );

        var match = fileContexts.Any( tag => activeTags.Contains( tag ) );

        if ( !match )
        {
            _logger.LogInformation(
                "Resource file skipped: ActiveContext `{active}` does not intersect file context `[{contexts}]`.",
                activeRaw, string.Join( ", ", fileContexts ) );
            return false;
        }

        return true;
    }

    private JsonNode? ResolveBody( Internal.Ast.StatementAst ast, JsonObject entry, int statementIndex, string? contextLabel )
    {
        var source = ExtractBodySource( ast );
        if ( source is null )
            return null;

        var label = contextLabel is null
            ? $"statements[{statementIndex}]"
            : $"statements[{statementIndex}] {contextLabel}";

        return source switch
        {
            BodyFileRef fileRef => LoadBodyFromResource( fileRef.Path, label ),
            BodyRef nameRef => ResolveNamedBody( entry, nameRef.Name, label ),
            _ => throw new InvalidOperationException( $"{label}: unsupported BodySource type `{source.GetType().Name}`." )
        };
    }

    private JsonNode? ResolveNamedBody( JsonObject entry, string name, string label )
    {
        // Form 2 (preferred): bodies.<name>. The `bodies` section's value
        // can itself be either an inline JSON object OR a `@path` file ref.
        var bodies = entry["bodies"] as JsonObject;
        var fromBodies = bodies?[name];
        if ( fromBodies is not null )
        {
            // If the bodies-section value is a string that starts with `@`,
            // treat it as a path reference. Otherwise it's the body itself.
            if ( fromBodies is JsonValue valueNode && valueNode.TryGetValue<string>( out var maybePath )
                 && maybePath.StartsWith( '@' ) )
            {
                return LoadBodyFromResource( maybePath[1..], $"{label} bodies.{name}" );
            }

            return JsonNode.Parse( fromBodies.ToJsonString() );
        }

        // Form 3 (back-compat): top-level sibling property. Preserves the
        // ADR-0009 / R-09 shape so existing migrations don't need to migrate.
        var sibling = entry[name];
        if ( sibling is not null )
            return JsonNode.Parse( sibling.ToJsonString() );

        throw new InvalidOperationException(
            $"{label}: `WITH BODY ${name}` not found. Expected `bodies.{name}` (preferred) or a top-level `{name}` sibling property." );
    }

    private JsonNode? LoadBodyFromResource( string path, string label )
    {
        // Convert path separators to embedded-resource dot notation. The
        // resource manifest name format is:
        //   <RootNamespace>.<MigrationFolder>.<sub>.<dirs>.<file>.json
        // ResourceHelper.GetResource prepends the assembly's ResourceLocation.
        var migrationName = Migration.VersionedName<TMigration>();
        var normalized = path.Replace( '\\', '/' );
        var resourceTail = normalized.Replace( '/', '.' );
        var resourceName = $"{migrationName}.{resourceTail}";

        string content;
        try
        {
            content = ResourceHelper.GetResource<TMigration>( resourceName );
        }
        catch ( Exception ex )
        {
            throw new InvalidOperationException(
                $"{label}: `WITH BODY @{path}` could not load embedded resource `{resourceName}`. " +
                $"Verify the file exists under the migration's resource folder AND is marked `EmbeddedResource` in the .csproj.", ex );
        }

        var parsed = JsonNode.Parse( content );
        if ( parsed is null )
            throw new InvalidOperationException(
                $"{label}: `WITH BODY @{path}` resolved to empty or invalid JSON." );
        return parsed;
    }

    private static BodySource? ExtractBodySource( Internal.Ast.StatementAst ast )
    {
        // Cast through the known body-bearing AST shapes. Each verb that
        // supports `WITH BODY` carries a BodySource on its record type.
        return ast switch
        {
            Internal.Ast.CreateIndexAst c => c.Body,
            Internal.Ast.ReindexAst r => r.Body,
            Internal.Ast.UpdateMappingAst um => um.Body,
            Internal.Ast.UpdateSettingsAst us => us.Body,
            Internal.Ast.CreateTemplateAst ct => ct.Body,
            Internal.Ast.CreateComponentAst cc => cc.Body,
            Internal.Ast.CreatePolicyAst cp => cp.Body,
            _ => null
        };
    }

    // R-20 — bulk-load helper. Wraps BulkAllObservable with the
    // production-safe defaults (8x parallelism, 1s exponential backoff,
    // 5 retries, refresh-once-at-end). Each retried 429 surfaces as a
    // structured WARN log so operator dashboards can spot
    // self-induced-throttling patterns.
    //
    // Per-batch refresh is intentionally disabled (BulkAllDescriptor sets
    // it to false on each request); the single-refresh-at-end path is
    // the documented production pattern. Authors who need per-batch
    // refresh have bigger correctness concerns and should hand-roll.

    public Task BulkLoadAsync<T>(
        string indexName,
        IEnumerable<T> documents,
        BulkLoadOptions? options = null,
        CancellationToken cancellationToken = default )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty( indexName );
        ArgumentNullException.ThrowIfNull( documents );

        var opts = options ?? new BulkLoadOptions();

        var bulkAll = _client.BulkAll( documents, b => b
            .Index( indexName )
            .Size( opts.BatchSize )
            .MaxDegreeOfParallelism( opts.MaxDegreeOfParallelism )
            .BackOffRetries( opts.BackOffRetries )
            .BackOffTime( opts.InitialBackOff )
            .RefreshOnCompleted( opts.RefreshOnCompleted )
            .ContinueAfterDroppedDocuments( false ),
            cancellationToken );

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously );

        var observer = new BulkAllObserver(
            onNext: response =>
            {
                if ( response.Retries > 0 )
                {
                    _logger.LogWarning(
                        "Bulk load: page {page} succeeded after {retries} retries (batch size {size} on `{idx}`)",
                        response.Page, response.Retries, opts.BatchSize, indexName );
                }
            },
            onError: ex =>
            {
                _logger.LogError( ex,
                    "Bulk load against `{idx}` failed after {retries} retry tier(s).",
                    indexName, opts.BackOffRetries );
                tcs.TrySetException( ex );
            },
            onCompleted: () =>
            {
                _logger.LogInformation( "Bulk load to `{idx}` completed.", indexName );
                tcs.TrySetResult( true );
            } );

        bulkAll.Subscribe( observer );

        return tcs.Task;
    }

    // Lightweight inline IObserver — avoids pulling in a full Rx wrapper
    // for one bulk-load helper.
    private sealed class BulkAllObserver : IObserver<global::OpenSearch.Client.BulkAllResponse>
    {
        private readonly Action<global::OpenSearch.Client.BulkAllResponse> _onNext;
        private readonly Action<Exception> _onError;
        private readonly Action _onCompleted;

        public BulkAllObserver(
            Action<global::OpenSearch.Client.BulkAllResponse> onNext,
            Action<Exception> onError,
            Action onCompleted )
        {
            _onNext = onNext;
            _onError = onError;
            _onCompleted = onCompleted;
        }

        public void OnNext( global::OpenSearch.Client.BulkAllResponse value ) => _onNext( value );
        public void OnError( Exception error ) => _onError( error );
        public void OnCompleted() => _onCompleted();
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
