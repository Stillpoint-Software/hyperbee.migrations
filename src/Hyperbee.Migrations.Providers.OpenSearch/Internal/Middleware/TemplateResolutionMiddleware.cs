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

public sealed class TemplateResolutionMiddleware
{
    public async Task<JsonNode?> ResolveAsync(
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

        return ExtractTemplateBlock( response.Body, templateRef.TemplateName );
    }

    // Pure JSON shape extraction; split out for unit testing without a live
    // cluster. Returns the inner `template` JSON block or throws if the
    // response shape doesn't match.
    public static JsonNode? ExtractTemplateBlock( string responseBody, string templateName )
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

        var template = templates[0]?["index_template"]?["template"];
        return template?.DeepClone();
    }
}
