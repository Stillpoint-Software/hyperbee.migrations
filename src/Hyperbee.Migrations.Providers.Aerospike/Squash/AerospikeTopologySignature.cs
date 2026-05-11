using System.Globalization;
using Aerospike.Client;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Aerospike topology signature per ADR-0019. Captures the deployment axes that
/// affect squash codegen determinism: server major/minor, namespace name, and
/// the namespace's structural configuration (replication-factor, default-ttl,
/// nsup-period, memory-size, storage-engine, cluster-name).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsCompatibleWith"/> rules:
/// <list type="bullet">
///   <item>Server major must match exactly. Aerospike majors can change the
///         shape of info-protocol responses and add/remove statement types.</item>
///   <item>Namespace name must match exactly. Namespaces are server-config
///         resources, not migration-created; a different name is a different
///         deployment.</item>
///   <item>Structural namespace properties (replication-factor, storage-engine,
///         memory-size) must match exactly. These are not changed by migrations
///         but a divergence between source and target signals a deployment
///         mistake the operator must resolve before squashing.</item>
/// </list>
/// Server minor differences are tolerated -- the info-protocol surface is
/// stable across minors within a major.
/// </para>
/// </remarks>
public sealed record AerospikeTopologySignature : ITopologySignature
{
    public const string ProviderIdValue = "aerospike";

    public int SchemaVersion => 1;
    public string ProviderId => ProviderIdValue;

    public int ServerMajor { get; init; }
    public int ServerMinor { get; init; }
    public string Namespace { get; init; } = "";
    public int ReplicationFactor { get; init; }
    public int DefaultTtl { get; init; }
    public int NsupPeriod { get; init; }
    public long MemorySize { get; init; }
    public string StorageEngine { get; init; } = "";
    public string ClusterName { get; init; } = "";

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["server_major"] = ServerMajor.ToString( CultureInfo.InvariantCulture ),
        ["server_minor"] = ServerMinor.ToString( CultureInfo.InvariantCulture ),
        ["namespace"] = Namespace,
        ["replication_factor"] = ReplicationFactor.ToString( CultureInfo.InvariantCulture ),
        ["default_ttl"] = DefaultTtl.ToString( CultureInfo.InvariantCulture ),
        ["nsup_period"] = NsupPeriod.ToString( CultureInfo.InvariantCulture ),
        ["memory_size"] = MemorySize.ToString( CultureInfo.InvariantCulture ),
        ["storage_engine"] = StorageEngine,
        ["cluster_name"] = ClusterName
    };

    public bool IsCompatibleWith( ITopologySignature other, out string reason )
    {
        if ( other is not AerospikeTopologySignature ar )
        {
            reason = $"signature is `{other?.ProviderId ?? "<null>"}`, not `{ProviderIdValue}`";
            return false;
        }

        if ( ar.ServerMajor != ServerMajor )
        {
            reason = $"server_major differs (this={ServerMajor}, other={ar.ServerMajor})";
            return false;
        }

        if ( !string.Equals( ar.Namespace, Namespace, StringComparison.Ordinal ) )
        {
            reason = $"namespace differs (this='{Namespace}', other='{ar.Namespace}')";
            return false;
        }

        if ( ar.ReplicationFactor != ReplicationFactor )
        {
            reason = $"replication_factor differs (this={ReplicationFactor}, other={ar.ReplicationFactor})";
            return false;
        }

        if ( !string.Equals( ar.StorageEngine, StorageEngine, StringComparison.Ordinal ) )
        {
            reason = $"storage_engine differs (this='{StorageEngine}', other='{ar.StorageEngine}')";
            return false;
        }

        if ( ar.MemorySize != MemorySize )
        {
            reason = $"memory_size differs (this={MemorySize}, other={ar.MemorySize})";
            return false;
        }

        if ( ar.DefaultTtl != DefaultTtl )
        {
            reason = $"default_ttl differs (this={DefaultTtl}, other={ar.DefaultTtl})";
            return false;
        }

        if ( ar.NsupPeriod != NsupPeriod )
        {
            reason = $"nsup_period differs (this={NsupPeriod}, other={ar.NsupPeriod})";
            return false;
        }

        if ( !string.Equals( ar.ClusterName, ClusterName, StringComparison.Ordinal ) )
        {
            reason = $"cluster_name differs (this='{ClusterName}', other='{ar.ClusterName}')";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Captures the live Aerospike cluster's topology axes via the info protocol.
    /// Probes the first connected node for `build`, `cluster-name`, and
    /// `namespace/&lt;namespace&gt;`. Namespace name is supplied by the caller so
    /// the signature reflects the operator's effective deployment scope.
    /// </summary>
    public static Task<AerospikeTopologySignature> CaptureAsync(
        IAerospikeClient client,
        string @namespace,
        CancellationToken cancellationToken = default )
    {
        if ( client == null )
            throw new ArgumentNullException( nameof( client ) );

        if ( string.IsNullOrWhiteSpace( @namespace ) )
            throw new ArgumentException( "Namespace is required.", nameof( @namespace ) );

        cancellationToken.ThrowIfCancellationRequested();

        var node = client.Nodes.FirstOrDefault()
            ?? throw new MigrationException(
                "Aerospike topology capture failed: no connected nodes. " +
                "Verify the cluster is available before squashing." );

        var responses = Info.Request( null, node, "build", "cluster-name", $"namespace/{@namespace}" );

        if ( !responses.TryGetValue( "build", out var buildRaw ) || string.IsNullOrEmpty( buildRaw ) )
            throw new MigrationException( "Aerospike info `build` returned empty; cannot determine server version." );

        if ( !responses.TryGetValue( $"namespace/{@namespace}", out var namespaceRaw ) || string.IsNullOrEmpty( namespaceRaw ) )
            throw new MigrationException( $"Aerospike info `namespace/{@namespace}` returned empty; verify the namespace is configured on this cluster." );

        var (major, minor) = ParseBuildVersion( buildRaw );
        var namespaceProps = ParseInfoMap( namespaceRaw );

        responses.TryGetValue( "cluster-name", out var clusterName );

        return Task.FromResult( new AerospikeTopologySignature
        {
            ServerMajor = major,
            ServerMinor = minor,
            Namespace = @namespace,
            ReplicationFactor = ReadInt( namespaceProps, "replication-factor" ),
            DefaultTtl = ReadInt( namespaceProps, "default-ttl" ),
            NsupPeriod = ReadInt( namespaceProps, "nsup-period" ),
            MemorySize = ReadLong( namespaceProps, "memory-size" ),
            StorageEngine = namespaceProps.TryGetValue( "storage-engine", out var se ) ? se : "",
            ClusterName = clusterName ?? ""
        } );
    }

    // Aerospike `build` returns a dotted version string, e.g. "6.4.0.1" or
    // "7.1.0.2". Parse the first two segments to (major, minor); ignore the rest.
    internal static (int major, int minor) ParseBuildVersion( string build )
    {
        var parts = build.Split( '.' );

        if ( parts.Length < 2 ||
             !int.TryParse( parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major ) ||
             !int.TryParse( parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor ) )
        {
            throw new MigrationException( $"Aerospike `build` info response is not a recognized version: '{build}'." );
        }

        return (major, minor);
    }

    // Aerospike info responses for namespace/<ns> are semicolon-separated
    // key=value pairs: `replication-factor=2;default-ttl=2592000;...`.
    internal static IReadOnlyDictionary<string, string> ParseInfoMap( string response )
    {
        var map = new Dictionary<string, string>( StringComparer.Ordinal );

        foreach ( var pair in response.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
        {
            var eq = pair.IndexOf( '=' );
            if ( eq <= 0 )
                continue;

            var key = pair.Substring( 0, eq ).Trim();
            var value = pair.Substring( eq + 1 ).Trim();
            map[key] = value;
        }

        return map;
    }

    private static int ReadInt( IReadOnlyDictionary<string, string> map, string key )
    {
        if ( !map.TryGetValue( key, out var raw ) || string.IsNullOrEmpty( raw ) )
            return 0;

        return int.TryParse( raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v ) ? v : 0;
    }

    private static long ReadLong( IReadOnlyDictionary<string, string> map, string key )
    {
        if ( !map.TryGetValue( key, out var raw ) || string.IsNullOrEmpty( raw ) )
            return 0L;

        return long.TryParse( raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v ) ? v : 0L;
    }
}
