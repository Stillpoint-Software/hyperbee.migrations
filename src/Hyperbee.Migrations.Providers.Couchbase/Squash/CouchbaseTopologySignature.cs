using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Couchbase topology signature per ADR-0019. Captures the deployment axes
/// that affect squash codegen determinism: server major/minor version,
/// edition (Community vs Enterprise), services enabled (the union across
/// nodes), bucket name + type + replica count + memory quota, storage
/// backend (Couchstore vs Magma, EE-only).
/// </summary>
/// <remarks>
/// <para>
/// CE-vs-EE is the Edition-axis analogue for Couchbase. Enterprise has
/// features Community doesn't: Magma storage backend, encryption-at-rest,
/// XDCR, n1ql_feat_ctrl, role-based access control, eventing, analytics.
/// An EE-source squash applied to a CE target silently passes structural
/// compare then fails at runtime on any EE-only feature path.
/// </para>
/// <para>
/// <see cref="IsCompatibleWith"/> rules:
/// <list type="bullet">
///   <item>Server major must match exactly.</item>
///   <item>Edition must match exactly (Community vs Enterprise).</item>
///   <item>Services-enabled set must match exactly. The squash output may
///         reference any service feature (analytics views, FTS indexes,
///         eventing functions); a target lacking the service will fail
///         on apply.</item>
///   <item>Bucket name must match exactly (squash scope identifier).</item>
///   <item>Bucket type must match exactly. <c>membase</c>/<c>couchbase</c>
///         (persistent), <c>ephemeral</c> (memory-only), and <c>memcached</c>
///         (legacy) differ in supported features.</item>
///   <item>Storage backend must match exactly. <c>couchstore</c> vs
///         <c>magma</c> differ in performance characteristics and index
///         behavior.</item>
///   <item>Replica count must match exactly. Affects rebalance + GSI
///         placement.</item>
///   <item>Memory quota must match exactly.</item>
/// </list>
/// Server minor differences are tolerated.
/// </para>
/// </remarks>
public sealed record CouchbaseTopologySignature : ITopologySignature
{
    public const string ProviderIdValue = "couchbase";

    public int SchemaVersion => 1;
    public string ProviderId => ProviderIdValue;

    public int ServerMajor { get; init; }
    public int ServerMinor { get; init; }

    /// <summary><c>Community</c> or <c>Enterprise</c>; empty when undetermined.</summary>
    public string Edition { get; init; } = "";

    /// <summary>
    /// Sorted union of services enabled across all cluster nodes:
    /// <c>kv</c>, <c>n1ql</c>, <c>index</c>, <c>fts</c>, <c>eventing</c>,
    /// <c>analytics</c>, <c>backup</c>, etc.
    /// </summary>
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();

    public string BucketName { get; init; } = "";

    /// <summary><c>membase</c>/<c>couchbase</c>, <c>ephemeral</c>, or <c>memcached</c>.</summary>
    public string BucketType { get; init; } = "";

    /// <summary>Couchstore vs Magma (EE-only). Empty when undetermined or CE.</summary>
    public string StorageBackend { get; init; } = "";

    public int ReplicaCount { get; init; }
    public long MemoryQuotaMB { get; init; }

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["server_major"] = ServerMajor.ToString( CultureInfo.InvariantCulture ),
        ["server_minor"] = ServerMinor.ToString( CultureInfo.InvariantCulture ),
        ["edition"] = Edition,
        ["services"] = string.Join( ",", Services.OrderBy( s => s, StringComparer.Ordinal ) ),
        ["bucket_name"] = BucketName,
        ["bucket_type"] = BucketType,
        ["storage_backend"] = StorageBackend,
        ["replica_count"] = ReplicaCount.ToString( CultureInfo.InvariantCulture ),
        ["memory_quota_mb"] = MemoryQuotaMB.ToString( CultureInfo.InvariantCulture )
    };

    public bool IsCompatibleWith( ITopologySignature other, out string reason )
    {
        if ( other is not CouchbaseTopologySignature cb )
        {
            reason = $"signature is `{other?.ProviderId ?? "<null>"}`, not `{ProviderIdValue}`";
            return false;
        }

        if ( cb.ServerMajor != ServerMajor )
        {
            reason = $"server_major differs (this={ServerMajor}, other={cb.ServerMajor})";
            return false;
        }

        if ( !string.Equals( cb.Edition, Edition, StringComparison.OrdinalIgnoreCase ) )
        {
            reason = $"edition differs (this='{Edition}', other='{cb.Edition}')";
            return false;
        }

        if ( !string.Equals( cb.BucketName, BucketName, StringComparison.Ordinal ) )
        {
            reason = $"bucket_name differs (this='{BucketName}', other='{cb.BucketName}')";
            return false;
        }

        if ( !string.Equals( cb.BucketType, BucketType, StringComparison.OrdinalIgnoreCase ) )
        {
            reason = $"bucket_type differs (this='{BucketType}', other='{cb.BucketType}')";
            return false;
        }

        if ( !string.Equals( cb.StorageBackend, StorageBackend, StringComparison.OrdinalIgnoreCase ) )
        {
            reason = $"storage_backend differs (this='{StorageBackend}', other='{cb.StorageBackend}')";
            return false;
        }

        if ( cb.ReplicaCount != ReplicaCount )
        {
            reason = $"replica_count differs (this={ReplicaCount}, other={cb.ReplicaCount})";
            return false;
        }

        if ( cb.MemoryQuotaMB != MemoryQuotaMB )
        {
            reason = $"memory_quota_mb differs (this={MemoryQuotaMB}, other={cb.MemoryQuotaMB})";
            return false;
        }

        var mine = new SortedSet<string>( Services, StringComparer.Ordinal );
        var theirs = new SortedSet<string>( cb.Services, StringComparer.Ordinal );
        if ( !mine.SetEquals( theirs ) )
        {
            var missingHere = string.Join( ",", theirs.Except( mine ) );
            var extraHere = string.Join( ",", mine.Except( theirs ) );
            reason =
                $"services set differs (other has [{(missingHere.Length > 0 ? missingHere : "<none>")}], " +
                $"this has [{(extraHere.Length > 0 ? extraHere : "<none>")}])";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Captures the live Couchbase cluster's topology axes via the REST API.
    /// </summary>
    public static async Task<CouchbaseTopologySignature> CaptureAsync(
        ICouchbaseRestApiService restApi,
        string bucketName,
        CancellationToken cancellationToken = default )
    {
        if ( restApi == null )
            throw new ArgumentNullException( nameof( restApi ) );

        if ( string.IsNullOrWhiteSpace( bucketName ) )
            throw new ArgumentException( "Bucket name is required.", nameof( bucketName ) );

        cancellationToken.ThrowIfCancellationRequested();

        var clusterDetails = await restApi.GetClusterDetailsAsync( cancellationToken ).ConfigureAwait( false )
            ?? throw new MigrationException(
                "Couchbase topology capture failed: `pools/default` returned null. " +
                "Verify the cluster is reachable and credentials are valid." );

        var (major, minor, edition) = ParseClusterDetails( clusterDetails );
        var services = clusterDetails is JsonObject clusterObj
            ? ParseServices( clusterObj )
            : Array.Empty<string>();

        cancellationToken.ThrowIfCancellationRequested();

        var bucketDetails = await restApi.GetBucketDetailsAsync( bucketName, cancellationToken ).ConfigureAwait( false )
            ?? throw new MigrationException(
                $"Couchbase topology capture failed: `pools/default/buckets/{bucketName}` returned null. " +
                "Verify the bucket exists." );

        var (bucketType, storageBackend, replicaCount, memoryQuotaMB) = ParseBucketDetails( bucketDetails );

        return new CouchbaseTopologySignature
        {
            ServerMajor = major,
            ServerMinor = minor,
            Edition = edition,
            Services = services,
            BucketName = bucketName,
            BucketType = bucketType,
            StorageBackend = storageBackend,
            ReplicaCount = replicaCount,
            MemoryQuotaMB = memoryQuotaMB
        };
    }

    // /pools/default response shape (relevant fields):
    //   {
    //     "isEnterprise": true,
    //     "nodes": [
    //       { "version": "7.2.0-5325-enterprise", "services": ["kv","n1ql","index",...] },
    //       ...
    //     ]
    //   }
    // Version is parsed from the first node's `version` field; edition is
    // taken from `isEnterprise` (preferred) with fallback to the version
    // suffix (`-enterprise` or `-community`).
    internal static (int major, int minor, string edition) ParseClusterDetails( JsonNode clusterDetails )
    {
        if ( clusterDetails is not JsonObject obj )
            throw new MigrationException( "Couchbase cluster details response is not a JSON object." );

        var edition = ParseEdition( obj );
        var (major, minor) = ParseVersionFromNodes( obj );

        return (major, minor, edition);
    }

    internal static string ParseEdition( JsonObject obj )
    {
        // Prefer the explicit `isEnterprise` boolean when present.
        if ( obj["isEnterprise"] is JsonValue isEnt && isEnt.TryGetValue<bool>( out var enterprise ) )
            return enterprise ? "Enterprise" : "Community";

        // Fall back to the version-suffix probe on the first node.
        var versionString = FirstNodeVersionString( obj );
        return NormalizeEditionFromVersion( versionString );
    }

    internal static string NormalizeEditionFromVersion( string versionString )
    {
        if ( string.IsNullOrEmpty( versionString ) )
            return "";
        if ( versionString.Contains( "enterprise", StringComparison.OrdinalIgnoreCase ) )
            return "Enterprise";
        if ( versionString.Contains( "community", StringComparison.OrdinalIgnoreCase ) )
            return "Community";
        return "";
    }

    internal static (int major, int minor) ParseVersionFromNodes( JsonObject obj )
    {
        var versionString = FirstNodeVersionString( obj )
            ?? throw new MigrationException( "Couchbase cluster details has no node version information." );
        return ParseVersionString( versionString );
    }

    private static string FirstNodeVersionString( JsonObject obj )
    {
        if ( obj["nodes"] is not JsonArray nodes || nodes.Count == 0 )
            return null;
        if ( nodes[0] is not JsonObject firstNode )
            return null;
        if ( firstNode["version"] is not JsonValue v || !v.TryGetValue<string>( out var versionString ) )
            return null;
        return versionString;
    }

    // Version string format: "7.2.0-5325-enterprise" or "7.2.0-5325-community"
    // or "7.2.0" (rare; very old releases). Take the first two dotted segments.
    internal static (int major, int minor) ParseVersionString( string raw )
    {
        var parts = raw.Split( new[] { '.', '-', '+' }, 4 );
        if ( parts.Length < 2 ||
             !int.TryParse( parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major ) ||
             !int.TryParse( parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor ) )
        {
            throw new MigrationException( $"Couchbase version string is not a recognized format: '{raw}'." );
        }
        return (major, minor);
    }

    internal static IReadOnlyList<string> ParseServices( JsonObject clusterDetails )
    {
        var services = new SortedSet<string>( StringComparer.Ordinal );

        if ( clusterDetails["nodes"] is not JsonArray nodes )
            return Array.Empty<string>();

        foreach ( var nodeNode in nodes )
        {
            if ( nodeNode is not JsonObject node )
                continue;
            if ( node["services"] is not JsonArray nodeServices )
                continue;
            foreach ( var svc in nodeServices )
            {
                if ( svc is JsonValue sv && sv.TryGetValue<string>( out var name ) && !string.IsNullOrWhiteSpace( name ) )
                    services.Add( name );
            }
        }

        return services.Count > 0 ? services.ToArray() : Array.Empty<string>();
    }

    // /pools/default/buckets/<name> response shape (relevant fields):
    //   {
    //     "bucketType": "membase" | "ephemeral" | "memcached",
    //     "storageBackend": "couchstore" | "magma",   // EE only
    //     "replicaNumber": 1,
    //     "quota": { "ram": 268435456, "rawRAM": 268435456 }    // bytes
    //   }
    internal static (string bucketType, string storageBackend, int replicaCount, long memoryQuotaMB)
        ParseBucketDetails( JsonNode bucketDetails )
    {
        if ( bucketDetails is not JsonObject obj )
            throw new MigrationException( "Couchbase bucket details response is not a JSON object." );

        var bucketType = obj["bucketType"] is JsonValue bt && bt.TryGetValue<string>( out var btStr ) ? btStr : "";
        var storageBackend = obj["storageBackend"] is JsonValue sb && sb.TryGetValue<string>( out var sbStr ) ? sbStr : "";
        var replicaCount = obj["replicaNumber"] is JsonValue rn && rn.TryGetValue<int>( out var rnInt ) ? rnInt : 0;

        // quota.ram is in bytes; report in MB rounded-down so the signature
        // matches the operator's intent (typically configured in MB).
        long memoryQuotaMB = 0;
        if ( obj["quota"] is JsonObject quotaObj
             && quotaObj["ram"] is JsonValue ramVal
             && ramVal.TryGetValue<long>( out var ramBytes ) )
        {
            memoryQuotaMB = ramBytes / (1024L * 1024L);
        }

        return (bucketType, storageBackend, replicaCount, memoryQuotaMB);
    }
}
