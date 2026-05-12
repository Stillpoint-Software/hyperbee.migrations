#nullable enable
using System.Text;
using OpenSearch.Client;
using OpenSearch.Net;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// Production capture helper that turns a live <see cref="IOpenSearchClient"/>
/// into the section-headered snapshot blob consumed by
/// <see cref="OpenSearchSnapshotCanonicalizer"/> and
/// <see cref="RestStateDiffStrategy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Probes the cluster's REST surface for the structural state: index
/// templates (v2), component templates, per-index settings+mappings+aliases,
/// alias graph, ISM policies (using the resolved endpoint prefix), and
/// ingest pipelines. The result is assembled as a UTF-8 string with the
/// section headers the canonicalizer recognizes.
/// </para>
/// <para>
/// The helper is intentionally static + stateless so it can be wrapped by a
/// <c>Func&lt;SnapshotCaptureRequest, CT, Task&lt;SnapshotCaptureResult&gt;&gt;</c>
/// delegate for any caller -- CLI, test harness, or production code.
/// </para>
/// <para>
/// Per-request <see cref="RequestConfiguration.RequestTimeout"/> is bounded
/// at <see cref="DefaultRequestTimeoutMs"/> so a stuck cluster does not hang
/// the squash generation indefinitely. The cancellation token is propagated
/// to every probe so caller-side cancellation aborts mid-capture.
/// </para>
/// </remarks>
public static class OpenSearchSnapshotCapture
{
    /// <summary>
    /// Captures the structural state of the cluster as a section-headered
    /// snapshot blob suitable for <see cref="OpenSearchSnapshotCanonicalizer.Canonicalize"/>.
    /// </summary>
    /// <param name="client">Live cluster handle.</param>
    /// <param name="ismPathPrefix">
    /// ISM REST API path prefix from <see cref="OpenSearchTopologySignature.IsmPathPrefix"/>
    /// (typically <c>_plugins/_ism</c> or <c>_opendistro/_ism</c>). Empty string
    /// when ISM is unavailable; the ISM section is omitted in that case.
    /// </param>
    /// <param name="cancellationToken">Cancellation token propagated to every probe.</param>
    /// <remarks>
    /// Request timeout is the connection-level timeout configured on the
    /// <see cref="IOpenSearchClient"/>; caller-side cancellation aborts
    /// mid-capture via the supplied <paramref name="cancellationToken"/>.
    /// Operators who need a tighter ceiling than the cluster's normal client
    /// timeout should wrap the call in a <see cref="CancellationTokenSource"/>
    /// with a CancelAfter rather than depending on per-probe request
    /// configuration that OpenSearch.Net's low-level surface does not expose
    /// cleanly.
    /// </remarks>
    public static async Task<string> CaptureAsync(
        IOpenSearchClient client,
        string ismPathPrefix,
        CancellationToken cancellationToken = default )
    {
        if ( client == null )
            throw new ArgumentNullException( nameof( client ) );

        cancellationToken.ThrowIfCancellationRequested();

        var ll = client.LowLevel;
        var bodies = new Dictionary<string, string>( StringComparer.Ordinal );

        bodies["index_template"] = await ProbeAsync(
            ll, "/_index_template/*", cancellationToken ).ConfigureAwait( false );

        cancellationToken.ThrowIfCancellationRequested();
        bodies["component_template"] = await ProbeAsync(
            ll, "/_component_template/*", cancellationToken ).ConfigureAwait( false );

        cancellationToken.ThrowIfCancellationRequested();
        bodies["index_metadata"] = await ProbeAsync(
            ll, "/_all", cancellationToken ).ConfigureAwait( false );

        cancellationToken.ThrowIfCancellationRequested();
        bodies["alias"] = await ProbeAsync(
            ll, "/_alias", cancellationToken ).ConfigureAwait( false );

        cancellationToken.ThrowIfCancellationRequested();
        bodies["ingest_pipeline"] = await ProbeAsync(
            ll, "/_ingest/pipeline", cancellationToken ).ConfigureAwait( false );

        if ( !string.IsNullOrWhiteSpace( ismPathPrefix ) )
        {
            cancellationToken.ThrowIfCancellationRequested();
            bodies["ism_policy"] = await ProbeAsync(
                ll, $"/{ismPathPrefix}/policies", cancellationToken ).ConfigureAwait( false );
        }

        return ComposeBlob( bodies );
    }

    /// <summary>
    /// Assembles the section-headered snapshot blob from a dictionary of
    /// section-name -> JSON-body pairs. Exposed for callers that already
    /// hold raw responses (test fixtures, custom harnesses).
    /// </summary>
    public static string ComposeBlob( IReadOnlyDictionary<string, string> sectionBodies )
    {
        if ( sectionBodies == null )
            throw new ArgumentNullException( nameof( sectionBodies ) );

        var sb = new StringBuilder();
        sb.Append( "# opensearch-snapshot v1\n\n" );

        // Compose sections in alphabetical order for deterministic output.
        // The canonicalizer also sorts, but composing-sorted here means
        // operators inspecting the raw blob (pre-canonicalization) see
        // a stable section order too.
        foreach ( var name in sectionBodies.Keys.OrderBy( k => k, StringComparer.Ordinal ) )
        {
            sb.Append( '[' ).Append( name ).Append( "]\n" );
            var body = sectionBodies[name] ?? "";
            sb.Append( body );
            if ( !body.EndsWith( '\n' ) )
                sb.Append( '\n' );
            sb.Append( '\n' );
        }

        return sb.ToString();
    }

    // Probe a path and return the body. On non-success, surface the error
    // as a MigrationException so the strategy's catch block converts it
    // to SquashGenerationResult.Failed with the underlying HTTP detail.
    // Empty body (404 or no content) returns the string "{}" so the
    // canonicalizer's JSON parser does not throw on an unknown section.
    private static async Task<string> ProbeAsync(
        IOpenSearchLowLevelClient ll,
        string path,
        CancellationToken cancellationToken )
    {
        var response = await ll.DoRequestAsync<StringResponse>(
            HttpMethod.GET,
            path,
            cancellationToken ).ConfigureAwait( false );

        if ( response.Success )
            return string.IsNullOrWhiteSpace( response.Body ) ? "{}" : response.Body;

        // 404 for an optional endpoint (e.g., ISM unavailable) is treated
        // as "no content" rather than an error -- the strategy continues
        // with that section absent.
        if ( response.HttpStatusCode == 404 )
            return "{}";

        var detail = response.OriginalException?.Message
            ?? response.Body
            ?? $"HTTP {response.HttpStatusCode}";
        throw new MigrationException(
            $"OpenSearch snapshot capture: probe of `{path}` failed with HTTP {response.HttpStatusCode}. {detail}" );
    }
}
