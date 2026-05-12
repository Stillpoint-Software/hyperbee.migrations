#nullable enable
using System.Globalization;
using System.Text.Json;
using Hyperbee.Migrations.Squash;
using OpenSearch.Client;
using OpenSearch.Net;
using OpenSearch.Net.Specification.CatApi;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// OpenSearch topology signature per ADR-0019. Captures the deployment axes
/// that affect squash codegen determinism: cluster major/minor version,
/// distribution flavor (OpenSearch vs Elasticsearch), cluster name, node
/// count, installed plugins, and ISM endpoint prefix (modern vs legacy).
/// </summary>
/// <remarks>
/// <para>
/// The plugin matrix is the contract's first multi-dimensional Edition-axis
/// pressure test. Aerospike had a single Community/Enterprise boolean;
/// OpenSearch has N plugin axes (security, ISM, k-NN, alerting,
/// anomaly-detection, etc.). If the plugin set on the squash-source cluster
/// differs from the apply-target cluster, the squash output may reference
/// features the target cannot honor -- replaying silently passes structural
/// compare then fails at runtime when, e.g., an ISM policy is applied
/// against a cluster lacking the ISM plugin.
/// </para>
/// <para>
/// <see cref="IsCompatibleWith"/> rules:
/// <list type="bullet">
///   <item>Server major must match exactly. Major-version differences can
///         change the index template / component template / mapping shape.</item>
///   <item>Distribution must match exactly. OpenSearch and Elasticsearch
///         diverged in 1.0; their feature surfaces are not interchangeable.</item>
///   <item>ISM endpoint prefix must match exactly. Modern (`_plugins/_ism`)
///         and legacy (`_opendistro/_ism`) imply different server generations.</item>
///   <item>Plugin set must match exactly. The squash output may reference
///         any installed plugin; a target lacking it will fail on apply.</item>
/// </list>
/// Server minor differences are tolerated -- the REST surface is stable
/// across minors within a major.
/// </para>
/// </remarks>
public sealed record OpenSearchTopologySignature : ITopologySignature
{
    public const string ProviderIdValue = "opensearch";

    public int SchemaVersion => 1;
    public string ProviderId => ProviderIdValue;

    public int ServerMajor { get; init; }
    public int ServerMinor { get; init; }

    /// <summary>
    /// Distribution flavor: <c>opensearch</c>, <c>elasticsearch</c>, or other.
    /// OpenSearch's root <c>/</c> response reports <c>version.distribution</c>;
    /// Elasticsearch (pre-fork) omits the field, and we record
    /// <c>elasticsearch</c> in that case.
    /// </summary>
    public string Distribution { get; init; } = "";

    public string ClusterName { get; init; } = "";

    public int NodeCount { get; init; }

    /// <summary>
    /// Sorted list of installed plugin component names from
    /// <c>GET /_cat/plugins?format=json</c>. Deduplicated across nodes
    /// (homogeneous clusters report identical sets per node; heterogeneous
    /// clusters take the union).
    /// </summary>
    public IReadOnlyList<string> Plugins { get; init; } = Array.Empty<string>();

    /// <summary>
    /// ISM REST API path prefix this cluster exposes: <c>_plugins/_ism</c>
    /// (modern OpenSearch 1.0+), <c>_opendistro/_ism</c> (legacy AWS Managed
    /// + pre-1.0 distributions), or empty when ISM is unavailable.
    /// </summary>
    public string IsmPathPrefix { get; init; } = "";

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["server_major"] = ServerMajor.ToString( CultureInfo.InvariantCulture ),
        ["server_minor"] = ServerMinor.ToString( CultureInfo.InvariantCulture ),
        ["distribution"] = Distribution,
        ["cluster_name"] = ClusterName,
        ["node_count"] = NodeCount.ToString( CultureInfo.InvariantCulture ),
        ["plugins"] = string.Join( ",", Plugins.OrderBy( p => p, StringComparer.Ordinal ) ),
        ["ism_path_prefix"] = IsmPathPrefix
    };

    public bool IsCompatibleWith( ITopologySignature other, out string reason )
    {
        if ( other is not OpenSearchTopologySignature os )
        {
            reason = $"signature is `{other?.ProviderId ?? "<null>"}`, not `{ProviderIdValue}`";
            return false;
        }

        if ( os.ServerMajor != ServerMajor )
        {
            reason = $"server_major differs (this={ServerMajor}, other={os.ServerMajor})";
            return false;
        }

        if ( !string.Equals( os.Distribution, Distribution, StringComparison.OrdinalIgnoreCase ) )
        {
            reason = $"distribution differs (this='{Distribution}', other='{os.Distribution}')";
            return false;
        }

        if ( !string.Equals( os.IsmPathPrefix, IsmPathPrefix, StringComparison.Ordinal ) )
        {
            reason = $"ism_path_prefix differs (this='{IsmPathPrefix}', other='{os.IsmPathPrefix}'). Modern (`_plugins/_ism`) and legacy (`_opendistro/_ism`) prefixes imply different server generations.";
            return false;
        }

        if ( !string.Equals( os.ClusterName, ClusterName, StringComparison.Ordinal ) )
        {
            reason = $"cluster_name differs (this='{ClusterName}', other='{os.ClusterName}')";
            return false;
        }

        // Plugin matrix: strict set equality. Any plugin difference is a
        // material incompatibility because the squash output may reference
        // plugin-provided features (ISM policies, k-NN mapping types,
        // alerting monitors, etc.).
        var mine = new SortedSet<string>( Plugins, StringComparer.Ordinal );
        var theirs = new SortedSet<string>( os.Plugins, StringComparer.Ordinal );
        if ( !mine.SetEquals( theirs ) )
        {
            var missingHere = string.Join( ",", theirs.Except( mine ) );
            var extraHere = string.Join( ",", mine.Except( theirs ) );
            reason =
                $"plugin set differs (other has [{(missingHere.Length > 0 ? missingHere : "<none>")}], " +
                $"this has [{(extraHere.Length > 0 ? extraHere : "<none>")}])";
            return false;
        }

        reason = null!;
        return true;
    }

    /// <summary>
    /// Captures the live OpenSearch cluster's topology axes via REST probes.
    /// Probes root <c>/</c> (version + distribution), <c>_cluster/health</c>
    /// (cluster name + node count), <c>_cat/plugins?format=json</c> (plugin
    /// matrix), and the two ISM endpoint candidates (modern, legacy).
    /// </summary>
    public static async Task<OpenSearchTopologySignature> CaptureAsync(
        IOpenSearchClient client,
        CancellationToken cancellationToken = default )
    {
        if ( client == null )
            throw new ArgumentNullException( nameof( client ) );

        cancellationToken.ThrowIfCancellationRequested();

        var ll = client.LowLevel;

        // Probe root for version + distribution.
        var rootResp = await ll.DoRequestAsync<StringResponse>(
            HttpMethod.GET, "/", cancellationToken ).ConfigureAwait( false );

        if ( !rootResp.Success )
            throw new MigrationException(
                $"OpenSearch topology capture: probe of `/` failed with HTTP {rootResp.HttpStatusCode}. " +
                $"Verify the cluster is reachable. Detail: {rootResp.OriginalException?.Message ?? rootResp.Body ?? "<empty>"}" );

        var (major, minor, distribution) = ParseRootResponse( rootResp.Body );

        // Cluster health for cluster_name + node count.
        var healthResp = await client.Cluster.HealthAsync(
            ct: cancellationToken ).ConfigureAwait( false );

        if ( !healthResp.IsValid )
            throw new MigrationException(
                $"OpenSearch topology capture: cluster health probe failed. " +
                $"Detail: {healthResp.OriginalException?.Message ?? "<empty>"}" );

        var clusterName = healthResp.ClusterName ?? "";
        var nodeCount = healthResp.NumberOfNodes;

        // Plugin matrix via _cat/plugins. Pass `format=json` via the typed
        // CatPluginsRequestParameters.Format property (OpenSearch.Net's
        // DoRequestAsync rejects query-strings embedded in the path; the
        // low-level Cat namespace routes the parameter to the request URI
        // correctly via the strongly-typed request-parameters object).
        var pluginsParams = new CatPluginsRequestParameters { Format = "json" };
        var pluginsResp = await ll.Cat.PluginsAsync<StringResponse>(
            pluginsParams, cancellationToken ).ConfigureAwait( false );

        var plugins = pluginsResp.Success
            ? ParsePluginsResponse( pluginsResp.Body )
            : Array.Empty<string>();

        // ISM endpoint detection: modern path first; on 404 try legacy; on
        // anything else (5xx, auth, network) record empty so the operator
        // can resolve before squashing. Mirrors IsmEndpointDetectStep's
        // probe order but without throwing -- topology capture should
        // surface "ISM unavailable" via the empty-prefix value rather than
        // fail the entire signature.
        var ismPrefix = await DetectIsmPrefixAsync( ll, cancellationToken ).ConfigureAwait( false );

        return new OpenSearchTopologySignature
        {
            ServerMajor = major,
            ServerMinor = minor,
            Distribution = distribution,
            ClusterName = clusterName,
            NodeCount = nodeCount,
            Plugins = plugins,
            IsmPathPrefix = ismPrefix
        };
    }

    // OpenSearch root response shape (relevant fields):
    //   { "name": "...", "cluster_name": "...",
    //     "version": { "distribution": "opensearch", "number": "2.13.0", ... } }
    // Elasticsearch pre-fork lacks `version.distribution`; in that case
    // we record "elasticsearch" so cross-fork attempts fail compat.
    internal static (int major, int minor, string distribution) ParseRootResponse( string? body )
    {
        if ( string.IsNullOrWhiteSpace( body ) )
            throw new MigrationException( "OpenSearch root response was empty; cannot determine server version." );

        using var doc = JsonDocument.Parse( body );
        var root = doc.RootElement;

        if ( !root.TryGetProperty( "version", out var version ) )
            throw new MigrationException( "OpenSearch root response missing `version` field." );

        if ( !version.TryGetProperty( "number", out var number ) || number.ValueKind != JsonValueKind.String )
            throw new MigrationException( "OpenSearch root response `version.number` is missing or not a string." );

        var (major, minor) = ParseVersionNumber( number.GetString() ?? "" );

        var distribution = "elasticsearch";
        if ( version.TryGetProperty( "distribution", out var dist ) && dist.ValueKind == JsonValueKind.String )
        {
            distribution = (dist.GetString() ?? "").ToLowerInvariant();
            if ( string.IsNullOrEmpty( distribution ) )
                distribution = "elasticsearch";
        }

        return (major, minor, distribution);
    }

    // OpenSearch version strings: "2.13.0", "2.13.0-SNAPSHOT". Take the first
    // two dotted segments. Pre-release suffixes after the third segment are
    // ignored for topology comparison.
    internal static (int major, int minor) ParseVersionNumber( string raw )
    {
        var parts = raw.Split( new[] { '.', '-', '+' }, 4 );

        if ( parts.Length < 2 ||
             !int.TryParse( parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major ) ||
             !int.TryParse( parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor ) )
        {
            throw new MigrationException( $"OpenSearch version number is not a recognized format: '{raw}'." );
        }

        return (major, minor);
    }

    // `GET /_cat/plugins?format=json` response shape:
    //   [ { "name": "node-1", "component": "opensearch-security", "version": "..." },
    //     { "name": "node-1", "component": "opensearch-index-management", ... } ]
    // Deduplicate by component (homogeneous clusters list per-node; we take
    // the union of components installed anywhere).
    internal static IReadOnlyList<string> ParsePluginsResponse( string? body )
    {
        if ( string.IsNullOrWhiteSpace( body ) )
            return Array.Empty<string>();

        using var doc = JsonDocument.Parse( body );
        if ( doc.RootElement.ValueKind != JsonValueKind.Array )
            return Array.Empty<string>();

        var components = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( var entry in doc.RootElement.EnumerateArray() )
        {
            if ( entry.ValueKind != JsonValueKind.Object )
                continue;
            if ( !entry.TryGetProperty( "component", out var c ) || c.ValueKind != JsonValueKind.String )
                continue;
            var name = c.GetString();
            if ( !string.IsNullOrWhiteSpace( name ) )
                components.Add( name! );
        }

        return components.ToArray();
    }

    private static async Task<string> DetectIsmPrefixAsync(
        IOpenSearchLowLevelClient ll,
        CancellationToken cancellationToken )
    {
        const string modernPrefix = "_plugins/_ism";
        const string legacyPrefix = "_opendistro/_ism";

        var modernResp = await ll.DoRequestAsync<StringResponse>(
            HttpMethod.GET, $"{modernPrefix}/policies", cancellationToken ).ConfigureAwait( false );

        if ( modernResp.Success )
            return modernPrefix;

        // 404 is the "wrong prefix" signal; try legacy. Other failures
        // (5xx, auth) mean ISM is unreachable regardless of prefix.
        if ( modernResp.HttpStatusCode != 404 )
            return "";

        var legacyResp = await ll.DoRequestAsync<StringResponse>(
            HttpMethod.GET, $"{legacyPrefix}/policies", cancellationToken ).ConfigureAwait( false );

        return legacyResp.Success ? legacyPrefix : "";
    }
}
