#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
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
    private readonly TemplateResolutionMiddleware _templateResolver;
    private readonly IsmEndpointCapability _ismCapability;

    // R-15a: cluster version is fetched once per dispatcher lifetime and
    // cached. The dispatcher is per-resource-runner so this cache is bounded
    // and there's no cross-runner sharing risk. Lazy<Task<>> serializes the
    // first fetch under contention without explicit locking.
    private Lazy<Task<Version>>? _clusterVersionCache;

    public StatementDispatcher( SafeDefaultMergeMiddleware merger )
        : this( merger, new TemplateResolutionMiddleware(), new IsmEndpointCapability() )
    {
    }

    public StatementDispatcher( SafeDefaultMergeMiddleware merger, TemplateResolutionMiddleware templateResolver )
        : this( merger, templateResolver, new IsmEndpointCapability() )
    {
    }

    public StatementDispatcher(
        SafeDefaultMergeMiddleware merger,
        TemplateResolutionMiddleware templateResolver,
        IsmEndpointCapability ismCapability )
    {
        _merger = merger;
        _templateResolver = templateResolver;
        _ismCapability = ismCapability;
    }

    // R-21 #3 — Resolves the ISM API path prefix. The bootstrap step
    // populates IsmEndpointCapability; if the dispatcher is constructed
    // without bootstrap (e.g., a test that bypasses Initialize), the
    // unresolved capability falls back to the modern path so non-AWS
    // single-node OpenSearch deployments work without explicit setup.
    private string IsmPathPrefix
        => _ismCapability.IsmPathPrefix ?? IsmEndpointDetectStep.ModernPrefix;

    public Task<StatementResult> DispatchAsync( StatementAst ast, StatementContext context )
    {
        return ast switch
        {
            WhenVersionAst wv => DispatchWhenVersionAsync( wv, context ),
            CompositeStatementAst comp => DispatchCompositeAsync( comp, context ),
            CreateIndexAst c => DispatchCreateIndexAsync( c, context ),
            DropIndexAst d => DispatchDropIndexAsync( d, context ),
            UpdateMappingAst um => DispatchUpdateMappingAsync( um, context ),
            UpdateSettingsAst us => DispatchUpdateSettingsAsync( us, context ),
            RefreshAst r => DispatchRefreshAsync( r, context ),
            WaitForHealthAst w => DispatchWaitForHealthAsync( w, context ),
            WaitUntilTaskAst wt => DispatchWaitUntilTaskAsync( wt, context ),
            ReindexAst rx => DispatchReindexAsync( rx, context ),
            AliasSwapAst aliasSwap => DispatchAliasSwapAsync( aliasSwap, context ),
            AliasAddAst aliasAdd => DispatchAliasAddAsync( aliasAdd, context ),
            AliasRemoveAst aliasRemove => DispatchAliasRemoveAsync( aliasRemove, context ),
            CreateTemplateAst ct => DispatchCreateTemplateAsync( ct, context ),
            CreateComponentAst cc => DispatchCreateComponentAsync( cc, context ),
            DropTemplateAst dt => DispatchDropTemplateAsync( dt, context ),
            DropComponentAst dc => DispatchDropComponentAsync( dc, context ),
            CreatePolicyAst cp => DispatchCreatePolicyAsync( cp, context ),
            ApplyPolicyAst ap => DispatchApplyPolicyAsync( ap, context ),
            _ => throw new InvalidOperationException(
                $"StatementDispatcher does not handle AST type {ast.GetType().Name}." )
        };
    }

    // --- WHEN VERSION <op> '<version>' <statement> ---
    //
    // R-15a: evaluate the cluster's reported version against the predicate;
    // dispatch the child only when the predicate holds. The cluster version
    // is fetched lazily (once per dispatcher) to avoid hitting the cluster
    // for every guarded statement.

    private async Task<StatementResult> DispatchWhenVersionAsync( WhenVersionAst ast, StatementContext context )
    {
        var verb = ast.Verb;

        Version clusterVersion;
        try
        {
            clusterVersion = await GetClusterVersionAsync( context ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: $"WHEN VERSION: cluster version probe failed: {ex.Message}",
                Exception: ex );
        }

        var predicate = ast.Evaluate( clusterVersion );
        if ( !predicate )
        {
            context.Logger.LogInformation(
                "{verb} skipped: cluster version {actual} does not satisfy `{op} {expected}`",
                verb, clusterVersion, ast.Op, ast.Version );
            return new StatementResult( StatementOutcome.Skipped, verb,
                Detail: $"WHEN VERSION: cluster {clusterVersion} does not satisfy {ast.Op} {ast.Version}; child {ast.Child.Verb} not dispatched" );
        }

        return await DispatchAsync( ast.Child, context ).ConfigureAwait( false );
    }

    private Task<Version> GetClusterVersionAsync( StatementContext context )
    {
        // Initialize the lazy on first request. The Lazy<Task<>> guarantees a
        // single concurrent fetch even under parallel dispatches.
        var cache = _clusterVersionCache ??= new Lazy<Task<Version>>( () => FetchClusterVersionAsync( context ) );
        return cache.Value;
    }

    private static async Task<Version> FetchClusterVersionAsync( StatementContext context )
    {
        var ll = context.Client.LowLevel;
        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.GET, string.Empty, context.CancellationToken ).ConfigureAwait( false );

        if ( !response.Success )
        {
            throw new InvalidOperationException(
                $"GET / failed: HTTP {response.HttpStatusCode}; body: {response.Body}" );
        }

        using var doc = JsonDocument.Parse( response.Body );
        if ( !doc.RootElement.TryGetProperty( "version", out var versionElement ) ||
             !versionElement.TryGetProperty( "number", out var numberElement ) )
        {
            throw new InvalidOperationException(
                $"GET / response did not include `version.number`; body: {response.Body}" );
        }

        var raw = numberElement.GetString() ?? throw new InvalidOperationException(
            "GET / response had `version.number` but it was null." );

        // The cluster's reported number is the same canonical form the parser
        // accepts (MAJOR.MINOR.PATCH). Trim to handle any whitespace; reject
        // suffixes here too — if a deployment ever reports a non-canonical
        // version, surface that loudly rather than truncating.
        var trimmed = raw.Trim();
        if ( trimmed.Contains( '-' ) )
        {
            // Strip the suffix for comparison purposes; the parser-side
            // version is already suffix-free. This is the one place we
            // tolerate cluster-side divergence (deploys do report `-SNAPSHOT`
            // sometimes) without rejecting outright — the comparison still
            // works against the underlying numeric tuple.
            trimmed = trimmed[..trimmed.IndexOf( '-' )];
        }

        if ( !Version.TryParse( trimmed, out var version ) )
        {
            throw new InvalidOperationException(
                $"Cluster-reported version `{raw}` did not parse as MAJOR.MINOR[.PATCH]." );
        }

        return version;
    }

    // --- composite (MIGRATE INDEX, etc.) ---
    //
    // R-30: a composite verb decomposes at parse time into an ordered sequence
    // of foundation-verb children. Walk them sequentially and halt on the first
    // failure. The composite's outcome reflects the last dispatched child:
    // Executed if all succeeded, Failed if any failed (subsequent children are
    // skipped). Skipped children (IF [NOT] EXISTS guards) do not halt the chain.
    //
    // R-19 partial-rollback ledger semantics — tracking which child failed for
    // `--force-resume` recovery — lands in a later slice (plan task 2.10).

    private async Task<StatementResult> DispatchCompositeAsync( CompositeStatementAst ast, StatementContext context )
    {
        var compositeVerb = ast.Verb;
        var details = new List<string>( ast.Children.Length );

        for ( var i = 0; i < ast.Children.Length; i++ )
        {
            var child = ast.Children[i];
            var childResult = await DispatchAsync( child, context ).ConfigureAwait( false );

            details.Add( $"[{i + 1}/{ast.Children.Length}] {child.Verb}: {childResult.Outcome} ({childResult.Detail})" );

            if ( childResult.Outcome == StatementOutcome.Failed )
            {
                return new StatementResult( StatementOutcome.Failed, compositeVerb,
                    Detail: $"{compositeVerb} halted at child {i + 1}/{ast.Children.Length} ({child.Verb}); {string.Join( " | ", details )}",
                    OpenSearchResponseStatus: childResult.OpenSearchResponseStatus,
                    Exception: childResult.Exception );
            }
        }

        return new StatementResult( StatementOutcome.Executed, compositeVerb,
            Detail: $"{compositeVerb} completed {ast.Children.Length} children: {string.Join( " | ", details )}" );
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

        // Resolve template-source body if the AST carries a TemplateBodyRef
        // (set by the MIGRATE INDEX composite expansion per R-30). This is the
        // runtime template-resolution point: parsing stays offline-pure
        // (ADR-0015), while the actual `GET /_index_template/<id>` happens
        // here, immediately before the CREATE INDEX request is built. The
        // resolved body becomes the input to SafeDefaultMergeMiddleware so
        // dynamic:strict injection (R-17) and composed_of-aware skipping still
        // apply against the live template body.
        var resolvedBody = context.ResolvedBody;
        var astForMerge = ast;
        if ( ast.TemplateBody is not null )
        {
            TemplateResolution resolution;
            try
            {
                resolution = await _templateResolver.ResolveAsync(
                    ll, ast.TemplateBody, context.CancellationToken ).ConfigureAwait( false );
            }
            catch ( Exception ex )
            {
                return new StatementResult( StatementOutcome.Failed, verb,
                    Detail: $"template `{ast.TemplateBody.TemplateName}` resolution failed: {ex.Message}",
                    Exception: ex );
            }

            resolvedBody = resolution.Body;

            // R-17 component-template-aware refinement: when the source
            // template references component templates via `composed_of`, the
            // resolved body alone does NOT carry the component mappings —
            // CREATE INDEX with an explicit body bypasses cluster-side
            // template-matching. Injecting `dynamic: strict` over an
            // incomplete body would override what the components were
            // expected to provide. Skip the injection on this path; emit a
            // WARN so the gap is visible in logs (the destination index will
            // not inherit component mappings — author should consider
            // creating the destination by name and letting cluster-side
            // template-matching apply via index_patterns).
            if ( resolution.HasComposedOf )
            {
                context.Logger.LogWarning(
                    "{verb}: template `{template}` references component templates via composed_of; " +
                    "skipping dynamic:strict injection for the destination index `{idx}`. " +
                    "Note: the destination will NOT inherit component mappings via this path because " +
                    "CREATE INDEX with an explicit body bypasses cluster-side template-matching. " +
                    "If you need component composition applied, create the destination index by name " +
                    "(no MIGRATE INDEX WITH TEMPLATE) and let an index_template's index_patterns match it.",
                    verb, ast.TemplateBody.TemplateName, ast.IndexName );

                astForMerge = ast with { InjectDynamicStrict = false };
            }
        }

        var merged = _merger.Merge( astForMerge, resolvedBody );
        var body = merged.ToJsonString();

        var response = await ll.Indices.CreateAsync<StringResponse>(
            ast.IndexName, PostData.String( body ), ctx: context.CancellationToken ).ConfigureAwait( false );

        var result = BuildResult( verb, response, $"created `{ast.IndexName}`" );

        if ( result.IsSuccess )
            await ImplicitWaitIfMutatingAsync( context, ast.IndexName, verb, ast.NoWaitJustification ).ConfigureAwait( false );

        return result;
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

        var result = BuildResult( verb, response, $"mapping updated on `{ast.IndexName}`" );

        // R-24c (c) — the "no reindex" gotcha. UPDATE MAPPING is additive at
        // the cluster level: the mapping definition changes for new documents,
        // but existing documents are NOT reanalyzed against the new mapping.
        // Authors who expect their analyzer / type / multi-field changes to
        // apply to existing data will hit silently-wrong query results until
        // they reindex. The diagnostic surfaces the gotcha at INFO so it's
        // visible in migration logs without blocking; for the canonical
        // mapping-propagation pattern, MIGRATE INDEX (R-30) does the
        // create-new + reindex + alias-swap dance.
        if ( result.IsSuccess )
        {
            context.Logger.LogInformation(
                "{verb} on `{idx}` succeeded. Note: mapping changes do NOT reindex existing documents — " +
                "fields/types changed in this update apply only to documents written after this point. " +
                "Use MIGRATE INDEX (R-30) to apply mapping changes to existing data.",
                verb, ast.IndexName );
        }

        return result;
    }

    // --- UPDATE SETTINGS [CLOSE] ---

    private async Task<StatementResult> DispatchUpdateSettingsAsync( UpdateSettingsAst ast, StatementContext context )
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

        var result = BuildResult( verb, dynamicResponse, $"settings updated on `{ast.IndexName}`" );

        if ( result.IsSuccess )
            await ImplicitWaitIfMutatingAsync( context, ast.IndexName, verb, ast.NoWaitJustification ).ConfigureAwait( false );

        return result;
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

        var result = BuildResult( verb, response, $"reindex {ast.Source} -> {ast.Destination}" );

        if ( result.IsSuccess )
            await ImplicitWaitIfMutatingAsync( context, ast.Destination, verb, ast.NoWaitJustification ).ConfigureAwait( false );

        return result;
    }

    // --- ALIAS SWAP <alias> FROM <old> TO <new> ---

    private async Task<StatementResult> DispatchAliasSwapAsync( AliasSwapAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        // Per R-16 / NF-2: atomic POST /_aliases with both actions in one body.
        // `must_exist: true` on the remove turns the precondition (alias points
        // at OldIndex) into a hard rejection — without it, OpenSearch silently
        // no-ops a remove of a non-existent alias and the swap appears to
        // succeed without actually moving anything. With must_exist, the
        // cluster atomically rejects the whole multi-action body if the
        // precondition fails. No separate GET-then-POST TOCTOU window.

        var body = $$"""
            {
              "actions": [
                { "remove": { "index": "{{ast.OldIndex}}", "alias": "{{ast.Alias}}", "must_exist": true } },
                { "add":    { "index": "{{ast.NewIndex}}", "alias": "{{ast.Alias}}" } }
              ]
            }
            """;

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.POST,
            "_aliases",
            context.CancellationToken,
            data: PostData.String( body ) ).ConfigureAwait( false );

        var result = BuildResult( verb, response, $"swapped `{ast.Alias}`: {ast.OldIndex} -> {ast.NewIndex}" );

        if ( result.IsSuccess )
            await ImplicitWaitIfMutatingAsync( context, ast.NewIndex, verb, ast.NoWaitJustification ).ConfigureAwait( false );

        return result;
    }

    // --- ALIAS ADD <alias> ON <idx> ---

    private static async Task<StatementResult> DispatchAliasAddAsync( AliasAddAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        var body = $$"""
            { "actions": [ { "add": { "index": "{{ast.IndexName}}", "alias": "{{ast.Alias}}" } } ] }
            """;

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.POST,
            "_aliases",
            context.CancellationToken,
            data: PostData.String( body ) ).ConfigureAwait( false );

        return BuildResult( verb, response, $"added alias `{ast.Alias}` -> `{ast.IndexName}`" );
    }

    // --- ALIAS REMOVE <alias> ON <idx> ---

    private static async Task<StatementResult> DispatchAliasRemoveAsync( AliasRemoveAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        var body = $$"""
            { "actions": [ { "remove": { "index": "{{ast.IndexName}}", "alias": "{{ast.Alias}}" } } ] }
            """;

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.POST,
            "_aliases",
            context.CancellationToken,
            data: PostData.String( body ) ).ConfigureAwait( false );

        return BuildResult( verb, response, $"removed alias `{ast.Alias}` from `{ast.IndexName}`" );
    }

    // --- CREATE TEMPLATE <name> [WITH BODY $body] ---

    private static async Task<StatementResult> DispatchCreateTemplateAsync( CreateTemplateAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( context.ResolvedBody is null )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: "CREATE TEMPLATE requires a body — supply WITH BODY $<name>.",
                Exception: new InvalidOperationException( "CREATE TEMPLATE with null body" ) );
        }

        var body = context.ResolvedBody.ToJsonString();

        // PUT /_index_template/<name> — composable index template.
        // Idempotent: PUT replaces an existing template definition.

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.PUT,
            $"_index_template/{ast.TemplateName}",
            context.CancellationToken,
            data: PostData.String( body ) ).ConfigureAwait( false );

        return BuildResult( verb, response, $"template `{ast.TemplateName}` created/updated" );
    }

    // --- CREATE COMPONENT <name> [WITH BODY $body] ---

    private static async Task<StatementResult> DispatchCreateComponentAsync( CreateComponentAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( context.ResolvedBody is null )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: "CREATE COMPONENT requires a body — supply WITH BODY $<name>.",
                Exception: new InvalidOperationException( "CREATE COMPONENT with null body" ) );
        }

        var body = context.ResolvedBody.ToJsonString();

        // PUT /_component_template/<name> — reusable building block referenced
        // by composable index templates via `composed_of`.

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.PUT,
            $"_component_template/{ast.ComponentName}",
            context.CancellationToken,
            data: PostData.String( body ) ).ConfigureAwait( false );

        return BuildResult( verb, response, $"component `{ast.ComponentName}` created/updated" );
    }

    // --- DROP TEMPLATE <name> [IF EXISTS] ---

    private static async Task<StatementResult> DispatchDropTemplateAsync( DropTemplateAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( ast.IfExists )
        {
            var existsResponse = await ll.DoRequestAsync<StringResponse>(
                global::OpenSearch.Net.HttpMethod.HEAD,
                $"_index_template/{ast.TemplateName}",
                context.CancellationToken ).ConfigureAwait( false );

            if ( existsResponse.HttpStatusCode != 200 )
            {
                context.Logger.LogInformation( "{verb} `{name}` skipped: IF EXISTS guard (not present)",
                    verb, ast.TemplateName );
                return new StatementResult( StatementOutcome.Skipped, verb,
                    Detail: $"IF EXISTS: template `{ast.TemplateName}` did not exist" );
            }
        }

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.DELETE,
            $"_index_template/{ast.TemplateName}",
            context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, response, $"template `{ast.TemplateName}` deleted" );
    }

    // --- DROP COMPONENT <name> [IF EXISTS] ---

    private static async Task<StatementResult> DispatchDropComponentAsync( DropComponentAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( ast.IfExists )
        {
            var existsResponse = await ll.DoRequestAsync<StringResponse>(
                global::OpenSearch.Net.HttpMethod.HEAD,
                $"_component_template/{ast.ComponentName}",
                context.CancellationToken ).ConfigureAwait( false );

            if ( existsResponse.HttpStatusCode != 200 )
            {
                context.Logger.LogInformation( "{verb} `{name}` skipped: IF EXISTS guard (not present)",
                    verb, ast.ComponentName );
                return new StatementResult( StatementOutcome.Skipped, verb,
                    Detail: $"IF EXISTS: component `{ast.ComponentName}` did not exist" );
            }
        }

        // The cluster returns 400 if the component is referenced by an index
        // template; the caller must drop the referencing template first. The
        // dispatcher surfaces that error verbatim via BuildResult.

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.DELETE,
            $"_component_template/{ast.ComponentName}",
            context.CancellationToken ).ConfigureAwait( false );

        return BuildResult( verb, response, $"component `{ast.ComponentName}` deleted" );
    }

    // --- CREATE POLICY <id> WITH BODY $body ---

    private async Task<StatementResult> DispatchCreatePolicyAsync( CreatePolicyAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        if ( context.ResolvedBody is null )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: "CREATE POLICY requires a body — supply WITH BODY $<name>.",
                Exception: new InvalidOperationException( "CREATE POLICY with null body" ) );
        }

        var body = context.ResolvedBody.ToJsonString();

        // PUT /_plugins/_ism/policies/<id> — Index State Management policy.

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.PUT,
            $"{IsmPathPrefix}/policies/{ast.PolicyId}",
            context.CancellationToken,
            data: PostData.String( body ) ).ConfigureAwait( false );

        return BuildResult( verb, response, $"policy `{ast.PolicyId}` created/updated" );
    }

    // --- APPLY POLICY <id> TO <pattern> ---

    private async Task<StatementResult> DispatchApplyPolicyAsync( ApplyPolicyAst ast, StatementContext context )
    {
        var verb = ast.Verb;
        var ll = context.Client.LowLevel;

        // POST /_plugins/_ism/add/<pattern> attaches a policy to existing
        // indices matching the pattern. `ism_template` matching only kicks in
        // at index-creation time, so this verb is the way to bind a policy to
        // already-created indices.
        //
        // ISM's add endpoint returns HTTP 200 even when zero indices were
        // updated (no matching indices, missing policy, already-attached) —
        // we have to inspect the response body's `updated_indices` and
        // `failures` fields and surface logical failures explicitly.

        var body = $$"""
            { "policy_id": "{{ast.PolicyId}}" }
            """;

        var response = await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.POST,
            $"{IsmPathPrefix}/add/{ast.IndexPattern}",
            context.CancellationToken,
            data: PostData.String( body ) ).ConfigureAwait( false );

        if ( !response.Success )
            return BuildResult( verb, response, $"policy `{ast.PolicyId}` apply to `{ast.IndexPattern}` failed" );

        try
        {
            using var doc = JsonDocument.Parse( response.Body );
            var root = doc.RootElement;

            var updated = root.TryGetProperty( "updated_indices", out var u ) ? u.GetInt32() : 0;
            var failures = root.TryGetProperty( "failures", out var f ) && f.GetBoolean();

            if ( failures || updated == 0 )
            {
                var detail = $"policy `{ast.PolicyId}` apply to `{ast.IndexPattern}`: updated {updated}, failures={failures}; body={response.Body}";
                return new StatementResult( StatementOutcome.Failed, verb,
                    Detail: detail,
                    OpenSearchResponseStatus: response.HttpStatusCode,
                    Exception: new InvalidOperationException( detail ) );
            }

            // R-12 lists APPLY POLICY among the mutating verbs that participate
            // in the implicit wait. The wait targets the index pattern; cluster
            // health endpoint accepts patterns natively.
            await ImplicitWaitIfMutatingAsync(
                context, ast.IndexPattern, verb, ast.NoWaitJustification ).ConfigureAwait( false );

            return new StatementResult( StatementOutcome.Executed, verb,
                Detail: $"policy `{ast.PolicyId}` applied to `{ast.IndexPattern}` ({updated} indices)",
                OpenSearchResponseStatus: response.HttpStatusCode );
        }
        catch ( JsonException ex )
        {
            return new StatementResult( StatementOutcome.Failed, verb,
                Detail: $"could not parse ISM add response: {ex.Message}",
                OpenSearchResponseStatus: response.HttpStatusCode,
                Exception: ex );
        }
    }

    // --- helpers ---

    // R-12: implicit cluster-health wait after mutating statements, scoped to the
    // mutated index per NF-3 (avoids stalling on permanently-yellow plugin indices
    // like .opendistro_security). Honors WaitMode:
    //   - PerStatement (SDK default): wait after each mutating statement
    //   - PerMigration (production via WithProductionDefaults): no per-statement
    //     wait; the dispatcher accumulates dirty indices in _dirtyIndices and
    //     the resource runner calls FlushImplicitWaitsAsync at end of migration
    //     for a single consolidated cluster-health call across all mutated indices
    //   - Off: no implicit waits — author owns explicit WAIT FOR statements

    // R-12 PerMigration tracking: every mutating dispatch records the index it
    // touched. The resource runner calls FlushImplicitWaitsAsync at end-of-
    // migration for a single consolidated _cluster/health/<idx1>,<idx2>... call
    // — avoids the N+1 health-check storm that PerStatement causes on long
    // migrations. ConcurrentBag isn't needed because dispatch is sequential
    // within a single resource runner; HashSet with no locking is correct.
    private readonly HashSet<string> _dirtyIndices = new( StringComparer.Ordinal );

    // Per-statement gate for the implicit wait. Returns whether the wait
    // happened (true) or was skipped (false, with reason logged where
    // appropriate). Mutating-verb dispatchers call this after a successful
    // cluster mutation; the AST's NoWaitJustification (when the author
    // writes `... NO WAIT("<reason>")`) suppresses the wait with a
    // structured WARN log.
    private async Task ImplicitWaitIfMutatingAsync(
        StatementContext context,
        string mutatedIndex,
        string verb,
        string? noWaitJustification = null )
    {
        if ( context.Options.WaitMode == WaitMode.Off )
            return;

        if ( noWaitJustification is not null )
        {
            // Per R-12: NO WAIT modifier with non-empty justification is the
            // documented per-statement opt-out. Structured WARN so PR review
            // and ops dashboards can grep `migration.no_wait` events. Under
            // PerMigration mode the per-statement wait is already a no-op
            // (only the end-of-migration flush runs), so NO WAIT degrades to
            // a DEBUG-level acknowledgement on that path.
            if ( context.Options.WaitMode == WaitMode.PerMigration )
            {
                context.Logger.LogDebug(
                    "migration.no_wait: {verb} on `{idx}` carries NO WAIT(\"{reason}\"); no-op under PerMigration (only end-of-migration flush runs)",
                    verb, mutatedIndex, noWaitJustification );
            }
            else
            {
                context.Logger.LogWarning(
                    "migration.no_wait: {verb} on `{idx}` skipped implicit wait per NO WAIT(\"{reason}\")",
                    verb, mutatedIndex, noWaitJustification );
            }
            return;
        }

        if ( context.Options.WaitMode == WaitMode.PerMigration )
        {
            // Accumulate; the consolidated wait runs in FlushImplicitWaitsAsync.
            _dirtyIndices.Add( mutatedIndex );
            return;
        }

        // PerStatement: existing per-call wait scoped to the mutated index.
        await ExecuteHealthWaitAsync( context, new[] { mutatedIndex } ).ConfigureAwait( false );
    }

    // R-12 PerMigration flush. Runs once at the end of a resource-runner pass
    // (called from OpenSearchResourceRunner.RunStatementsFromJsonAsync). A
    // no-op when WaitMode != PerMigration or when no dirty indices have been
    // accumulated. Best-effort: failure surfaces as WARN, not as an
    // exception, so a flaky cluster-health probe doesn't fail an otherwise-
    // successful migration.
    public async Task FlushImplicitWaitsAsync( StatementContext context )
    {
        if ( context.Options.WaitMode != WaitMode.PerMigration )
            return;

        if ( _dirtyIndices.Count == 0 )
            return;

        var indices = _dirtyIndices.ToArray();
        _dirtyIndices.Clear();

        context.Logger.LogInformation(
            "Per-migration consolidated wait: {count} dirty index(es) [{indices}]",
            indices.Length, string.Join( ",", indices ) );

        await ExecuteHealthWaitAsync( context, indices ).ConfigureAwait( false );
    }

    private static async Task ExecuteHealthWaitAsync( StatementContext context, IReadOnlyCollection<string> indices )
    {
        var threshold = context.Options.ClusterHealthThreshold == ClusterHealthThreshold.Green
            ? global::OpenSearch.Net.WaitForStatus.Green
            : global::OpenSearch.Net.WaitForStatus.Yellow;

        var timeout = context.Options.ImplicitWaitTimeout;

        try
        {
            await context.Client.Cluster.HealthAsync(
                selector: s => s
                    .WaitForStatus( threshold )
                    .Timeout( timeout )
                    .Index( global::OpenSearch.Client.Indices.Index( string.Join( ",", indices ) ) ),
                ct: context.CancellationToken
            ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            // Implicit waits are best-effort defense — they don't fail the
            // statement result. Log + continue. If a stronger guarantee is
            // needed, the author should write an explicit WAIT FOR statement.
            context.Logger.LogWarning( ex,
                "Implicit wait on [{indices}] failed; continuing", string.Join( ",", indices ) );
        }
    }


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
