using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Couchbase;
using Couchbase.Query;
using Hyperbee.Migrations.Providers.Couchbase.Services;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Production capture helper that combines two sources -- N1QL
/// <c>system:keyspaces</c> + <c>system:indexes</c> via
/// <see cref="ICluster.QueryAsync"/>, plus REST
/// <c>/pools/default/buckets/&lt;name&gt;</c> via
/// <see cref="ICouchbaseRestApiService.GetBucketDetailsAsync"/> -- into the
/// section-headered snapshot blob consumed by
/// <see cref="CouchbaseSnapshotCanonicalizer"/> and
/// <see cref="HybridStrategy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Probes the cluster for structural state:
/// <list type="bullet">
///   <item><c>SELECT * FROM system:keyspaces WHERE `bucket` = $bucket</c>
///         -- collections + scopes per bucket.</item>
///   <item><c>SELECT * FROM system:indexes WHERE keyspace_id = $bucket OR
///         bucket_id = $bucket</c> -- GSI definitions per bucket.</item>
///   <item><c>GET /pools/default/buckets/&lt;name&gt;</c> -- bucket settings
///         (storage backend, replicaNumber, evictionPolicy, conflictResolution,
///         compressionMode, history retention).</item>
/// </list>
/// Result is assembled as a UTF-8 string with section headers
/// (<c>[buckets]</c>, <c>[keyspaces]</c>, <c>[indexes]</c>).
/// </para>
/// <para>
/// <b>Settling wait:</b> the canonicalizer rejects transient index states
/// (<c>building</c>, <c>pending</c>). This capture helper does NOT itself
/// wait for indexes to settle -- the squash CLI is responsible for issuing
/// a settle wait between migration apply and snapshot capture (typically via
/// a BUILD INDEX + poll loop). The canonicalizer's loud rejection of
/// transient states catches the bug at squash-time if the wait is missing.
/// </para>
/// <para>
/// JSON-shape behavior: each <c>system:keyspaces</c> / <c>system:indexes</c>
/// row is captured as-is from the cluster (envelope: <c>keyspaces</c> or
/// <c>indexes</c> top-level field per Couchbase's standard N1QL row shape);
/// the canonicalizer downstream strips ephemerals (id, lastUsed, etc.) at
/// every nesting level.
/// </para>
/// </remarks>
public static class CouchbaseSnapshotCapture
{
    /// <summary>
    /// Captures the structural state of the supplied bucket as a section-
    /// headered snapshot blob suitable for
    /// <see cref="CouchbaseSnapshotCanonicalizer.Canonicalize"/>.
    /// </summary>
    public static async Task<string> CaptureAsync(
        ICluster cluster,
        ICouchbaseRestApiService restApi,
        string bucketName,
        CancellationToken cancellationToken = default )
    {
        ArgumentNullException.ThrowIfNull( cluster );
        ArgumentNullException.ThrowIfNull( restApi );
        if ( string.IsNullOrWhiteSpace( bucketName ) )
            throw new ArgumentException( "Bucket name is required.", nameof( bucketName ) );

        cancellationToken.ThrowIfCancellationRequested();

        // [buckets] section: REST API gives the full settings shape.
        var bucketDetails = await restApi.GetBucketDetailsAsync( bucketName, cancellationToken )
            .ConfigureAwait( false );

        // [keyspaces] section: scopes + collections via N1QL system table.
        var keyspaceRows = await QuerySystemRowsAsync(
            cluster,
            "SELECT keyspaces.* FROM system:keyspaces WHERE `bucket` = $bucket OR keyspace_id = $bucket",
            bucketName,
            cancellationToken ).ConfigureAwait( false );

        // [indexes] section: GSI definitions via N1QL system table.
        var indexRows = await QuerySystemRowsAsync(
            cluster,
            "SELECT indexes.* FROM system:indexes WHERE keyspace_id = $bucket OR bucket_id = $bucket",
            bucketName,
            cancellationToken ).ConfigureAwait( false );

        return ComposeBlob( bucketName, bucketDetails, keyspaceRows, indexRows );
    }

    /// <summary>
    /// Assembles the section-headered snapshot blob from already-captured
    /// data. Exposed for callers that already hold captured data (test
    /// fixtures, custom harnesses) and for unit-testing the assembly logic
    /// independently of a live cluster.
    /// </summary>
    public static string ComposeBlob(
        string bucketName,
        JsonNode bucketDetails,
        IReadOnlyList<JsonNode> keyspaceRows,
        IReadOnlyList<JsonNode> indexRows )
    {
        if ( string.IsNullOrWhiteSpace( bucketName ) )
            throw new ArgumentException( "Bucket name is required.", nameof( bucketName ) );

        var sb = new StringBuilder();
        sb.Append( "# couchbase-snapshot v1\n" );
        sb.Append( "# bucket: " ).Append( bucketName ).Append( '\n' );
        sb.Append( '\n' );

        // [buckets] section: REST details keyed by bucket name. Single bucket
        // per snapshot per the v1 contract.
        if ( bucketDetails != null )
        {
            sb.Append( "[buckets]\n" );
            var bucketsRoot = new JsonObject { [bucketName] = bucketDetails.DeepClone() };
            sb.Append( bucketsRoot.ToJsonString( IndentedWriter ) ).Append( '\n' );
            sb.Append( '\n' );
        }

        // [keyspaces] section: rows keyed by `keyspace_id` (the qualified
        // bucket/scope/collection identifier). Sort by keyspace_id for
        // byte-stability.
        if ( keyspaceRows != null && keyspaceRows.Count > 0 )
        {
            sb.Append( "[keyspaces]\n" );
            var keyspacesRoot = new JsonObject();
            foreach ( var row in keyspaceRows.OrderBy( IdentityOf, StringComparer.Ordinal ) )
                keyspacesRoot[IdentityOf( row )] = row.DeepClone();
            sb.Append( keyspacesRoot.ToJsonString( IndentedWriter ) ).Append( '\n' );
            sb.Append( '\n' );
        }

        // [indexes] section: rows keyed by composite identity (keyspace + name).
        if ( indexRows != null && indexRows.Count > 0 )
        {
            sb.Append( "[indexes]\n" );
            var indexesRoot = new JsonObject();
            foreach ( var row in indexRows.OrderBy( IndexIdentityOf, StringComparer.Ordinal ) )
                indexesRoot[IndexIdentityOf( row )] = row.DeepClone();
            sb.Append( indexesRoot.ToJsonString( IndentedWriter ) ).Append( '\n' );
            sb.Append( '\n' );
        }

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions IndentedWriter = new()
    {
        WriteIndented = true
    };

    // Returns the keyspace/index identity used as the sort key + map key.
    // For system:keyspaces this is `id`; for rows without a stable identifier
    // we fall back to the JSON serialization as a tiebreaker (deterministic
    // but opaque -- normal rows always have `id`).
    private static string IdentityOf( JsonNode row )
    {
        if ( row is JsonObject obj )
        {
            if ( obj.TryGetPropertyValue( "id", out var idNode ) && idNode is JsonValue idVal && idVal.TryGetValue<string>( out var s ) )
                return s;
            if ( obj.TryGetPropertyValue( "name", out var nameNode ) && nameNode is JsonValue nameVal && nameVal.TryGetValue<string>( out var n ) )
                return n;
        }
        return row?.ToJsonString() ?? "";
    }

    // For system:indexes the natural identity is (keyspace_id, scope_id, name).
    // Returns a composite key joined with '/' for stable sort + map insertion.
    private static string IndexIdentityOf( JsonNode row )
    {
        if ( row is JsonObject obj )
        {
            string bucket = TryStringValue( obj, "keyspace_id" ) ?? TryStringValue( obj, "bucket_id" ) ?? "";
            string scope = TryStringValue( obj, "scope_id" ) ?? "";
            string keyspace = TryStringValue( obj, "collection_id" ) ?? TryStringValue( obj, "keyspace_id" ) ?? "";
            string name = TryStringValue( obj, "name" ) ?? "";
            return $"{bucket}/{scope}/{keyspace}/{name}";
        }
        return row?.ToJsonString() ?? "";
    }

    private static string TryStringValue( JsonObject obj, string key )
    {
        if ( !obj.TryGetPropertyValue( key, out var node ) || node is not JsonValue val )
            return null;
        return val.TryGetValue<string>( out var s ) ? s : null;
    }

    private static async Task<List<JsonNode>> QuerySystemRowsAsync(
        ICluster cluster,
        string statement,
        string bucketName,
        CancellationToken cancellationToken )
    {
        var options = new QueryOptions()
            .Parameter( "bucket", bucketName )
            .CancellationToken( cancellationToken );

        var result = await cluster.QueryAsync<JsonObject>( statement, options ).ConfigureAwait( false );

        var rows = new List<JsonNode>();
        await foreach ( var row in result.ConfigureAwait( false ) )
        {
            if ( row != null )
                rows.Add( row );
        }
        return rows;
    }
}
