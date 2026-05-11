using System.Text;
using Aerospike.Client;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Production capture helper that turns a live <see cref="IAerospikeClient"/>
/// connection into the section-headered snapshot blob consumed by
/// <see cref="AerospikeSnapshotCanonicalizer"/> and <see cref="InfoSnapshotStrategy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Probes the first connected node for <c>sets/&lt;ns&gt;</c> and
/// <c>sindex/&lt;ns&gt;</c> via the Aerospike info protocol, then assembles
/// the result as a UTF-8 string with <c>[sets]</c> and <c>[sindex]</c>
/// section headers. The output format is the contract the canonicalizer
/// expects.
/// </para>
/// <para>
/// The helper is intentionally static + stateless so it can be wrapped by a
/// <c>Func&lt;SnapshotCaptureRequest, CT, Task&lt;SnapshotCaptureResult&gt;&gt;</c>
/// delegate for any caller -- CLI, test harness, or production code. The CLI
/// + the verification round both wire concrete capture delegates that call
/// this helper after applying migrations to an ephemeral container.
/// </para>
/// </remarks>
public static class AerospikeSnapshotCapture
{
    /// <summary>
    /// Captures the structural state of the supplied namespace as a snapshot
    /// blob suitable for <see cref="AerospikeSnapshotCanonicalizer.Canonicalize"/>.
    /// </summary>
    /// <param name="client">Live cluster handle. Must have at least one connected node.</param>
    /// <param name="namespace">Target namespace name.</param>
    /// <param name="cancellationToken">Cancellation token (info-protocol probes are synchronous; the token is checked before each request).</param>
    public static Task<string> CaptureAsync(
        IAerospikeClient client,
        string @namespace,
        CancellationToken cancellationToken = default )
    {
        if ( client == null )
            throw new ArgumentNullException( nameof( client ) );
        if ( string.IsNullOrWhiteSpace( @namespace ) )
            throw new ArgumentException( "namespace is required.", nameof( @namespace ) );

        cancellationToken.ThrowIfCancellationRequested();

        var node = client.Nodes.FirstOrDefault()
            ?? throw new MigrationException(
                "Aerospike snapshot capture failed: no connected nodes. " +
                "Verify the cluster is available before squashing." );

        var sets = SafeInfoRequest( node, $"sets/{@namespace}" );
        cancellationToken.ThrowIfCancellationRequested();
        var sindex = SafeInfoRequest( node, $"sindex/{@namespace}" );

        return Task.FromResult( ComposeBlob( @namespace, sets, sindex ) );
    }

    /// <summary>
    /// Assembles the <c>[sets]</c>/<c>[sindex]</c> blob the canonicalizer
    /// consumes. Exposed for callers that already hold raw Info responses
    /// (e.g., test fixtures, custom capture harnesses).
    /// </summary>
    public static string ComposeBlob( string @namespace, string setsResponse, string sindexResponse )
    {
        var sb = new StringBuilder();
        sb.Append( "# aerospike-snapshot v1\n" );
        sb.Append( "# namespace: " ).Append( @namespace ).Append( '\n' );
        sb.Append( '\n' );

        sb.Append( "[sets]\n" );
        sb.Append( setsResponse ?? string.Empty ).Append( '\n' );
        sb.Append( '\n' );

        sb.Append( "[sindex]\n" );
        sb.Append( sindexResponse ?? string.Empty ).Append( '\n' );

        return sb.ToString();
    }

    // Aerospike's `sets/<ns>` and `sindex/<ns>` info responses return an empty
    // string for a namespace with no sets / no secondary indexes. Treat a thrown
    // AerospikeException as an empty response (matches the codebase's
    // IndexExistsAsync convention in AerospikeClientExtensions).
    private static string SafeInfoRequest( Node node, string command )
    {
        try
        {
            return Info.Request( node, command ) ?? string.Empty;
        }
        catch ( AerospikeException )
        {
            return string.Empty;
        }
    }
}
