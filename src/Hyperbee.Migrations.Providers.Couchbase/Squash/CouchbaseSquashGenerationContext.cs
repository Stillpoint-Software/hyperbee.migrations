using Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Couchbase-specific squash generation context. Carries the live cluster +
/// REST API service + bucket name plus a delegate-injected snapshot capture
/// mechanism.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot capture is parameterized so the runtime library does not need a
/// hard Testcontainers dependency. The CLI tool and integration test suite
/// wire concrete capture functions; production wires a Testcontainers
/// Couchbase Server instance against which the migration range is applied
/// before the N1QL <c>system:keyspaces</c> + <c>system:indexes</c> probes
/// and REST <c>/pools/default/buckets/&lt;name&gt;</c> are called.
/// </para>
/// <para>
/// The context carries BOTH the N1QL <see cref="ICluster"/> (for
/// <c>system:keyspaces</c>/<c>system:indexes</c> queries + topology services
/// matrix) AND the <see cref="ICouchbaseRestApiService"/> (for bucket/scope
/// settings the N1QL system tables don't expose). This is the "hybrid"
/// nature of <c>HybridStrategy</c>: two capture sources combined into one
/// section-headered blob.
/// </para>
/// </remarks>
public sealed class CouchbaseSquashGenerationContext : ISquashGenerationContext
{
    public string ProviderId => CouchbaseTopologySignature.ProviderIdValue;
    public string SquashName { get; }
    public long SquashVersion { get; }

    /// <summary>Live Couchbase cluster client for the operator's cluster.</summary>
    public ICluster Cluster { get; }

    /// <summary>REST API service for bucket/scope settings the N1QL system tables omit.</summary>
    public ICouchbaseRestApiService RestApi { get; }

    /// <summary>Bucket scope for the squash (drives topology + snapshot scope).</summary>
    public string BucketName { get; }

    /// <summary>
    /// Captures a snapshot of an ephemeral Couchbase cluster after applying
    /// the supplied migration version range. Callers (CLI, test harness)
    /// inject a concrete implementation; production uses Testcontainers
    /// Couchbase Server + <see cref="CouchbaseSnapshotCapture.CaptureAsync"/>.
    /// </summary>
    public Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> CaptureSnapshotAsync { get; }

    public CouchbaseSquashGenerationContext(
        string squashName,
        long squashVersion,
        ICluster cluster,
        ICouchbaseRestApiService restApi,
        string bucketName,
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> captureSnapshotAsync )
    {
        if ( string.IsNullOrWhiteSpace( squashName ) )
            throw new ArgumentException( "squashName is required.", nameof( squashName ) );
        if ( squashVersion <= 0 )
            throw new ArgumentException( "squashVersion must be positive.", nameof( squashVersion ) );
        if ( string.IsNullOrWhiteSpace( bucketName ) )
            throw new ArgumentException( "bucketName is required.", nameof( bucketName ) );

        SquashName = squashName;
        SquashVersion = squashVersion;
        Cluster = cluster ?? throw new ArgumentNullException( nameof( cluster ) );
        RestApi = restApi ?? throw new ArgumentNullException( nameof( restApi ) );
        BucketName = bucketName;
        CaptureSnapshotAsync = captureSnapshotAsync ?? throw new ArgumentNullException( nameof( captureSnapshotAsync ) );
    }
}

/// <summary>
/// Inputs for a single snapshot capture round. Identifies the version range
/// to apply (inclusive) and the topology the ephemeral cluster must match.
/// </summary>
/// <param name="Label">
/// Caller-supplied label ("snapshot-A" / "snapshot-B" / "verifier") used for
/// log messages and (per ADR-0019 A18) container retention naming on failure.
/// </param>
/// <param name="UpToVersion">Apply migrations with Version &lt;= this value.</param>
/// <param name="RequiredTopology">Container must match these axes (per A10).</param>
public sealed record SnapshotCaptureRequest(
    string Label,
    long UpToVersion,
    CouchbaseTopologySignature RequiredTopology );

/// <summary>
/// Result of a single snapshot capture. <see cref="SnapshotBlob"/> is the
/// section-headered blob produced by the capture function; the canonicalizer
/// normalizes it downstream.
/// </summary>
public sealed record SnapshotCaptureResult(
    string SnapshotBlob );
