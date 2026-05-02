#nullable enable
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;

// Runtime middleware that resolves a TemplateBodyRef into the JSON body for a
// CREATE INDEX dispatch. Per ADR-0015, the parser is offline-pure — template
// references are carried through parse time as opaque names; this middleware
// performs the `GET /_index_template/<name>` immediately before the dispatcher
// builds the CREATE INDEX request.
//
// Used by the MIGRATE INDEX composite (R-30) when the author wrote
// `WITH TEMPLATE <id>` rather than supplying an inline body. The author can
// keep template definitions canonical in cluster state and propagate them to
// new indices without duplicating the body in the migration resource.
//
// Response shape (GET /_index_template/<name>):
//   {
//     "index_templates": [
//       {
//         "name": "<name>",
//         "index_template": {
//           "index_patterns": [...],
//           "template": { "settings": {...}, "mappings": {...}, "aliases": {...} },
//           "priority": 100,
//           "composed_of": [...]
//         }
//       }
//     ]
//   }
//
// We extract `index_templates[0].index_template.template` and use that as the
// CREATE INDEX request body. SafeDefaultMergeMiddleware still runs on top so
// dynamic:strict injection (R-17) and composed_of-aware skipping continue to
// apply against the resolved template body.

// Result of a template resolution. `Body` is the inner `template` JSON block
// (settings/mappings/aliases) destined for the CREATE INDEX request body;
// `HasComposedOf` is true when the source template references component
// templates via `composed_of`.
//
// `HasComposedOf` is the signal R-17 needs from this code path: when the
// source template composes components, the resolved body alone does not carry
// those component mappings (CREATE INDEX with an explicit body bypasses
// template-matching). Injecting `dynamic: strict` against an incomplete body
// would surprise authors whose component mappings define their own dynamic
// behavior. The dispatcher uses this signal to skip the injection — same
// semantics as the existing inline-body composed_of skip in
// SafeDefaultMergeMiddleware, lifted to the runtime-resolved path.

public readonly record struct TemplateResolution( JsonNode? Body, bool HasComposedOf );

public sealed class TemplateResolutionMiddleware
{
    public async Task<TemplateResolution> ResolveAsync(
        IOpenSearchLowLevelClient client,
        TemplateBodyRef templateRef,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( client );
        ArgumentNullException.ThrowIfNull( templateRef );

        var response = await client.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.GET,
            $"_index_template/{templateRef.TemplateName}",
            cancellationToken ).ConfigureAwait( false );

        if ( !response.Success )
        {
            var status = response.HttpStatusCode?.ToString() ?? "unknown";
            throw new InvalidOperationException(
                $"Template `{templateRef.TemplateName}` lookup failed: HTTP {status}; body: {response.Body}" );
        }

        return Extract( response.Body, templateRef.TemplateName );
    }

    // Pure JSON-shape extraction; split out for unit testing without a live
    // cluster. Returns the inner `template` block plus a flag indicating
    // whether the source `index_template` uses `composed_of`.
    public static TemplateResolution Extract( string responseBody, string templateName )
    {
        if ( string.IsNullOrEmpty( responseBody ) )
            throw new InvalidOperationException(
                $"Template `{templateName}`: empty response body." );

        JsonNode? root;
        try
        {
            root = JsonNode.Parse( responseBody );
        }
        catch ( Exception ex )
        {
            throw new InvalidOperationException(
                $"Template `{templateName}`: response was not valid JSON: {ex.Message}", ex );
        }

        var templates = root?["index_templates"]?.AsArray();
        if ( templates is null || templates.Count == 0 )
        {
            throw new InvalidOperationException(
                $"Template `{templateName}` not found in cluster response (no `index_templates` entries)." );
        }

        var indexTemplate = templates[0]?["index_template"];
        var template = indexTemplate?["template"];
        var composedOf = indexTemplate?["composed_of"]?.AsArray();
        var hasComposedOf = composedOf is not null && composedOf.Count > 0;

        return new TemplateResolution( template?.DeepClone(), hasComposedOf );
    }

    // Back-compat for tests/callers that just want the body. Delegates to
    // Extract and discards the composed_of flag.
    public static JsonNode? ExtractTemplateBlock( string responseBody, string templateName )
        => Extract( responseBody, templateName ).Body;
}
