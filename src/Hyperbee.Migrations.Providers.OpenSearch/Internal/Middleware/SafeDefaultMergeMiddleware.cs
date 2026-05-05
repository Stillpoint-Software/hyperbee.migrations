#nullable enable
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;

// Runtime middleware that merges parser-emitted safe-default flags into the
// JSON request body before HTTP dispatch.
//
// Architecture: per ADR-0011, parser owns intent (the AST flags) and runtime
// middleware owns execution (JSON tree merge). The merge logic lives here so
// that future verbs follow the same shape: AST flag → middleware merge rule.
//
// Per ADR-0015, this code runs at request-build time, NOT at parse time.

public sealed class SafeDefaultMergeMiddleware
{
    /// <summary>
    /// Merges safe-default flags from the AST into the request body.
    /// Returns the body that should be sent on the wire (a fresh tree;
    /// does not mutate the input).
    /// </summary>
    /// <param name="ast">Parser-emitted statement AST.</param>
    /// <param name="body">
    /// Sibling-resolved request body. Null is acceptable for verbs that can
    /// dispatch without a body (e.g., REINDEX with all defaults).
    /// </param>
    public JsonObject Merge( StatementAst ast, JsonNode? body )
    {
        return ast switch
        {
            CreateIndexAst createIndex => MergeCreateIndex( createIndex, body ),
            ReindexAst reindex => MergeReindex( reindex, body ),
            _ => throw new InvalidOperationException(
                $"SafeDefaultMergeMiddleware does not handle AST type {ast.GetType().Name}." )
        };
    }

    // CREATE INDEX merge rules (ADR-0011, R-17, PM-4 fix):
    //
    //   1. If InjectDynamicStrict is false: pass body through unchanged
    //   2. If body is null: produce { "mappings": { "dynamic": "strict" } }
    //   3. If body has `composed_of`: SKIP injection (component-template-aware)
    //      and emit InjectionDecision.SkippedComposedOf for diagnostics
    //   4. If body.mappings.dynamic is explicitly set: PRESERVE user value
    //      and emit InjectionDecision.PreservedExplicit
    //   5. Else: merge `dynamic: strict` into body.mappings (creating mappings
    //      object if absent)
    //
    // The middleware never mutates the caller's tree; clone first.

    private static JsonObject MergeCreateIndex( CreateIndexAst ast, JsonNode? body )
    {
        if ( !ast.InjectDynamicStrict )
            return CloneOrEmpty( body );

        var clone = CloneOrEmpty( body );

        if ( clone.ContainsKey( "composed_of" ) )
            return clone; // Decision: SkippedComposedOf

        if ( !clone.TryGetPropertyValue( "mappings", out var mappingsNode ) || mappingsNode is not JsonObject mappings )
        {
            mappings = new JsonObject();
            clone["mappings"] = mappings;
        }

        if ( mappings.ContainsKey( "dynamic" ) )
            return clone; // Decision: PreservedExplicit

        mappings["dynamic"] = "strict";
        return clone;
    }

    // REINDEX merge rules (ADR-0011, R-08a, PM-3 fix):
    //
    //   1. If InjectOpTypeCreate is false (UNSAFE branch): pass body through
    //      unchanged. Author owns idempotency.
    //   2. If body is null: produce
    //        { "source": { "index": <src> }, "dest": { "index": <dst>, "op_type": "create" }, "conflicts": "proceed" }
    //   3. If body has dest object missing op_type: merge `op_type: create` and `conflicts: proceed`
    //      (unless body already sets `conflicts`)
    //   4. If body has dest with `op_type: create` already: merge `conflicts: proceed`
    //      (idempotent inject)
    //   5. If body has dest with conflicting op_type (e.g., "index"): throw
    //      SafeDefaultConflictException — author must use REINDEX UNSAFE("...")
    //      to opt out
    //
    // `conflicts: proceed` is co-injected with `op_type: create` because the safe-default
    // semantics are "skip pre-existing docs" — aborting on the first conflict (the
    // OpenSearch default) would return HTTP 409 and defeat idempotency entirely.
    //
    // The middleware also ensures source.index and dest.index match the AST's
    // Source/Destination unless the body explicitly overrides them (advanced use).

    private static JsonObject MergeReindex( ReindexAst ast, JsonNode? body )
    {
        var clone = CloneOrEmpty( body );

        // Ensure source.index defaults from AST when body omits it
        var source = EnsureObject( clone, "source" );
        if ( !source.ContainsKey( "index" ) )
            source["index"] = ast.Source;

        var dest = EnsureObject( clone, "dest" );
        if ( !dest.ContainsKey( "index" ) )
            dest["index"] = ast.Destination;

        if ( !ast.InjectOpTypeCreate )
            return clone; // UNSAFE branch — author opt-out, no enforcement

        if ( !dest.TryGetPropertyValue( "op_type", out var existing ) || existing is null )
        {
            dest["op_type"] = "create";
            if ( !clone.ContainsKey( "conflicts" ) )
                clone["conflicts"] = "proceed";
            return clone;
        }

        var existingValue = existing.GetValue<string>();
        if ( existingValue == "create" )
        {
            if ( !clone.ContainsKey( "conflicts" ) )
                clone["conflicts"] = "proceed";
            return clone; // idempotent inject
        }

        throw new SafeDefaultConflictException(
            $"REINDEX body specifies `op_type: \"{existingValue}\"` which conflicts with the safe-default `op_type: create`. " +
            $"To opt out (author owns idempotency), prefix the verb: `REINDEX UNSAFE(\"<justification>\") FROM ... TO ...`." );
    }

    private static JsonObject CloneOrEmpty( JsonNode? body )
    {
        if ( body is null )
            return new JsonObject();

        // Deep-clone via round-trip — safe for arbitrary user JSON
        var clone = JsonNode.Parse( body.ToJsonString() );
        if ( clone is not JsonObject obj )
            throw new InvalidOperationException(
                $"Statement body must be a JSON object, got {body.GetValueKind()}." );
        return obj;
    }

    private static JsonObject EnsureObject( JsonObject parent, string key )
    {
        if ( parent.TryGetPropertyValue( key, out var existing ) && existing is JsonObject obj )
            return obj;

        var fresh = new JsonObject();
        parent[key] = fresh;
        return fresh;
    }
}

public sealed class SafeDefaultConflictException : Exception
{
    public SafeDefaultConflictException( string message ) : base( message ) { }
}
