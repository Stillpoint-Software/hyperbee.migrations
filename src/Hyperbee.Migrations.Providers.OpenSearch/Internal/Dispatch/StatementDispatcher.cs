#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Microsoft.Extensions.Logging;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;

// Statement dispatcher per ADR-0011 hybrid (parser owns intent, runtime owns
// execution). For each AST shape, this class:
//   1. Honors IF [NOT] EXISTS guards via HEAD probe (R-14)
//   2. Runs SafeDefaultMergeMiddleware for AST nodes that carry safe-default
//      flags (CREATE INDEX, REINDEX) — merges the flags into the body
//   3. Dispatches the resulting request via the OpenSearchClient low-level API
//      (raw JSON path) for body-bearing verbs; high-level API for parameterless
//      verbs where it's clearer
//   4. Returns a typed StatementResult for the resource runner to log/aggregate
//
// The dispatcher uses the low-level client throughout to avoid the
// ThrowExceptions divergence we discovered during Phase 1 validation —
// LowLevel calls return StringResponse with .Success / .HttpStatusCode,
// independent of the high-level client's ThrowExceptions setting.

public sealed class StatementDispatcher
{
    private readonly SafeDefaultMergeMiddleware _merger;

    public StatementDispatcher( SafeDefaultMergeMiddleware merger )
    {
        _merger = merger;
    }

    public Task<StatementResult> DispatchAsync( StatementAst ast, StatementContext context )
    {
        return ast switch
        {
            CreateIndexAst c => DispatchCreateIndexAsync( c, context ),
            DropIndexAst d => DispatchDropIndexAsync( d, context ),
            UpdateMappingAst um => DispatchUpdateMappingAsync( um, context ),
            UpdateSettingsAst us => DispatchUpdateSettingsAsync( us, context ),
            RefreshAst r => DispatchRefreshAsync( r, context ),
            WaitForHealthAst w => DispatchWaitForHealthAsync( w, context ),
            WaitUntilTaskAst wt => DispatchWaitUntilTaskAsync( wt, context ),
            ReindexAst rx => DispatchReindexAsync( rx, context ),
            _ => throw new InvalidOperationException(
                $"StatementDispatcher does not handle AST type {ast.GetType().Name}." )
        };
    }

    // --- CREATE INDEX ---

    private async Task<StatementResult> DispatchCreateIndexAsync( CreateIndexAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( ast.IfNotExists )
        {
            var existsResponse = await ll.Indices.ExistsAsync<StringResponse>(
                ast.IndexName, ctx: context.CancellationToken ).ConfigureAwait( false );

            if ( existsResponse.HttpStatusCode == 200 )
            {
                context.Logger.LogInformation( "{verb} `{idx}` skipped: IF NOT EXISTS guard (already present)",
                    verb, ast.IndexName );
                return new StatementResult( StatementOutcome.Skipped, verb,
                    Detail: $"IF NOT EXISTS: `{ast.IndexName}` already exists" );
            }
        }

        var merged = _merger.Merge( ast, context.ResolvedBody );
        var body = merged.ToJsonString();

        var response = await ll.Indices.CreateAsync<StringResponse>(
            ast.IndexName, PostData.String( body ), ctx: context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, response, $"created `{ast.IndexName}`" );
    }

    // --- DROP INDEX ---

    private static async Task<StatementResult> DispatchDropIndexAsync( DropIndexAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( ast.IfExists )
        {
            var existsResponse = await ll.Indices.ExistsAsync<StringResponse>(
                ast.IndexName, ctx: context.CancellationToken ).ConfigureAwait( false );

            if ( existsResponse.HttpStatusCode != 200 )
            {
                context.Logger.LogInformation( "{verb} `{idx}` skipped: IF EXISTS guard (not present)",
                    verb, ast.IndexName );
                return new StatementResult( StatementOutcome.Skipped, verb,
                    Detail: $"IF EXISTS: `{ast.IndexName}` did not exist" );
            }
        }

        var response = await ll.Indices.DeleteAsync<StringResponse>(
            ast.IndexName, ctx: context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, response, $"deleted `{ast.IndexName}`" );
    }

    // --- UPDATE MAPPING ---

    private static async Task<StatementResult> DispatchUpdateMappingAsync( UpdateMappingAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( context.ResolvedBody is null )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: "UPDATE MAPPING requires a body — supply WITH BODY $<name> in the statement.",
                Exception: new InvalidOperationException( "UPDATE MAPPING with null body" ) );
        }

        var body = context.ResolvedBody.ToJsonString();
        var response = await ll.Indices.PutMappingAsync<StringResponse>(
            ast.IndexName, PostData.String( body ), ctx: context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, response, $"mapping updated on `{ast.IndexName}`" );
    }

    // --- UPDATE SETTINGS [CLOSE] ---

    private static async Task<StatementResult> DispatchUpdateSettingsAsync( UpdateSettingsAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( context.ResolvedBody is null )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: "UPDATE SETTINGS requires a body — supply WITH BODY $<name>.",
                Exception: new InvalidOperationException( "UPDATE SETTINGS with null body" ) );
        }

        var body = context.ResolvedBody.ToJsonString();

        // CLOSE flag opts into close → update → open for static settings.
        // Without CLOSE, the cluster rejects static-setting changes; the user
        // must explicitly acknowledge the brief write-unavailability window.

        if ( ast.Close )
        {
            context.Logger.LogInformation( "{verb} CLOSE on `{idx}`: closing index for static settings update",
                verb, ast.IndexName );

            var closeResponse = await ll.Indices.CloseAsync<StringResponse>(
                ast.IndexName, ctx: context.CancellationToken ).ConfigureAwait( false );

            if ( !closeResponse.Success )
                return BuildResult( verb, closeResponse, $"close failed on `{ast.IndexName}`" );

            try
            {
                var settingsResponse = await ll.Indices.UpdateSettingsAsync<StringResponse>(
                    ast.IndexName, PostData.String( body ), ctx: context.CancellationToken ).ConfigureAwait( false );

                if ( !settingsResponse.Success )
                    return BuildResult( verb, settingsResponse, $"settings update failed on `{ast.IndexName}` (will reopen)" );
            }
            finally
            {
                // Always attempt to reopen, even if the settings update failed
                var openResponse = await ll.Indices.OpenAsync<StringResponse>(
                    ast.IndexName, ctx: context.CancellationToken ).ConfigureAwait( false );

                if ( !openResponse.Success )
                {
                    context.Logger.LogCritical(
                        "{verb} CLOSE-OPEN dance: index `{idx}` could not be reopened — manual intervention required",
                        verb, ast.IndexName );
                }
            }

            return new StatementResult( StatementOutcome.Executed, verb,
                Detail: $"settings updated on `{ast.IndexName}` (close-open dance)",
                OpenSearchResponseStatus: 200 );
        }

        var dynamicResponse = await ll.Indices.UpdateSettingsAsync<StringResponse>(
            ast.IndexName, PostData.String( body ), ctx: context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, dynamicResponse, $"settings updated on `{ast.IndexName}`" );
    }

    // --- REFRESH ---

    private static async Task<StatementResult> DispatchRefreshAsync( RefreshAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        var response = await ll.Indices.RefreshAsync<StringResponse>(
            ast.IndexName, ctx: context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, response, $"refreshed `{ast.IndexName}`" );
    }

    // --- WAIT FOR <green|yellow> ---

    private static async Task<StatementResult> DispatchWaitForHealthAsync( WaitForHealthAst ast, StatementContext context )
    {
        var verb = ast.Verb;

        var threshold = ast.Threshold == HealthStatus.Green
            ? global::OpenSearch.Net.WaitForStatus.Green
            : global::OpenSearch.Net.WaitForStatus.Yellow;

        var timeout = ast.Timeout ?? context.Options.ImplicitWaitTimeout;

        var response = await context.Client.Cluster.HealthAsync(
            selector: s =>
            {
                var sel = s.WaitForStatus( threshold ).Timeout( timeout );
                if ( ast.IndexName is not null )
                    sel = sel.Index( global::OpenSearch.Client.Indices.Index( ast.IndexName ) );
                return sel;
            },
            ct: context.CancellationToken
        ).ConfigureAwait( false );

        if ( !response.IsValid )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: $"WAIT FOR {threshold} failed: {response.OriginalException?.Message ?? response.DebugInformation}",
                OpenSearchResponseStatus: response.ApiCall?.HttpStatusCode,
                Exception: response.OriginalException );
        }

        if ( response.TimedOut )
        {
            var ex = new TimeoutException(
                $"WAIT FOR {threshold} timed out after {timeout} (observed status: {response.Status})." );
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: $"timed out at {response.Status}",
                OpenSearchResponseStatus: response.ApiCall?.HttpStatusCode,
                Exception: ex );
        }

        return new StatementResult( StatementOutcome.Executed, verb,
            Detail: $"reached {response.Status}",
            OpenSearchResponseStatus: response.ApiCall?.HttpStatusCode );
    }

    // --- WAIT UNTIL TASK <id> COMPLETE ---

    private static async Task<StatementResult> DispatchWaitUntilTaskAsync( WaitUntilTaskAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;
        var timeout = ast.Timeout ?? TimeSpan.FromMinutes( 30 );
        var deadline = context.TimeProvider.GetUtcNow() + timeout;

        // Exponential backoff polling: 500ms → 1s → 2s → ... → 30s ceiling.
        var pollDelay = TimeSpan.FromMilliseconds( 500 );
        var maxPollDelay = TimeSpan.FromSeconds( 30 );

        while ( true )
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var response = await ll.Tasks.GetTaskAsync<StringResponse>(
                ast.TaskId, ctx: context.CancellationToken ).ConfigureAwait( false );

            if ( !response.Success )
                return BuildResult( verb, response, $"task `{ast.TaskId}` lookup failed" );

            try
            {
                using var doc = JsonDocument.Parse( response.Body );
                if ( doc.RootElement.TryGetProperty( "completed", out var completed ) && completed.GetBoolean() )
                {
                    if ( doc.RootElement.TryGetProperty( "error", out var error ) && error.ValueKind != JsonValueKind.Null )
                    {
                        var errMsg = error.ToString();
                        return new StatementResult( StatementOutcome.Failed, verb,
                            Detail: $"task `{ast.TaskId}` completed with error: {errMsg}",
                            Exception: new InvalidOperationException( errMsg ) );
                    }

                    return new StatementResult( StatementOutcome.Executed, verb,
                        Detail: $"task `{ast.TaskId}` complete",
                        OpenSearchResponseStatus: response.HttpStatusCode );
                }
            }
            catch ( JsonException ex )
            {
                return new StatementResult( StatementOutcome.Failed, verb,
                    Detail: $"could not parse task response: {ex.Message}",
                    Exception: ex );
            }

            if ( context.TimeProvider.GetUtcNow() >= deadline )
            {
                return new StatementResult( StatementOutcome.Failed, verb,
                    Detail: $"task `{ast.TaskId}` did not complete within {timeout}",
                    Exception: new TimeoutException( $"WAIT UNTIL TASK timeout after {timeout}." ) );
            }

            await Task.Delay( pollDelay, context.TimeProvider, context.CancellationToken ).ConfigureAwait( false );
            pollDelay = TimeSpan.FromMilliseconds( Math.Min( pollDelay.TotalMilliseconds * 2, maxPollDelay.TotalMilliseconds ) );
        }
    }

    // --- REINDEX ---

    private async Task<StatementResult> DispatchReindexAsync( ReindexAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        var merged = _merger.Merge( ast, context.ResolvedBody );
        var body = merged.ToJsonString();

        // For Phase 1: synchronous reindex (the default). Async dispatch via Tasks API
        // is a Phase 2 enhancement (R-11) — authors who need it can compose with
        // WAIT UNTIL TASK once the runner exposes the task id.

        var response = await ll.ReindexOnServerAsync<StringResponse>(
            PostData.String( body ), ctx: context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, response, $"reindex {ast.Source} -> {ast.Destination}" );
    }

    // --- helpers ---

    private static StatementResult BuildResult( string verb, StringResponse response, string detail )
    {
        if ( response.Success )
            return new StatementResult( StatementOutcome.Executed, verb, detail, response.HttpStatusCode );

        var errMsg = response.OriginalException?.Message ?? response.Body ?? $"HTTP {response.HttpStatusCode}";
        return new StatementResult( StatementOutcome.Failed, verb,
            Detail: $"{detail} failed: {errMsg}",
            OpenSearchResponseStatus: response.HttpStatusCode,
            Exception: response.OriginalException );
    }
}
