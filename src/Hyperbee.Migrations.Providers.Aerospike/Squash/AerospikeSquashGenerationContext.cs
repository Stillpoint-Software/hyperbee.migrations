using Aerospike.Client;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Aerospike-specific squash generation context. Carries the live client
/// (used for topology capture from the operator's cluster) plus a
/// delegate-injected snapshot capture mechanism.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot capture is parameterized so the runtime library does not need a
/// hard Testcontainers dependency. The CLI tool and integration test suite
/// wire concrete capture functions; production wires a Testcontainers
/// Aerospike instance against which the migration range is applied before
/// <c>Info.Request</c> is called.
/// </para>
/// <para>
/// The capture function receives the migration metadata it needs (label +
/// upper-bound version + required topology axes per ADR-0019 A10) and returns
/// the raw <c>[sets]</c>/<c>[sindex]</c> snapshot blob ready for
/// <see cref="AerospikeSnapshotCanonicalizer"/>.
/// </para>
/// </remarks>
public sealed class AerospikeSquashGenerationContext : ISquashGenerationContext
{
    public string ProviderId => AerospikeTopologySignature.ProviderIdValue;
    public string SquashName { get; }
    public long SquashVersion { get; }

    /// <summary>Live Aerospike client for the operator's cluster.</summary>
    public IAerospikeClient Client { get; }

    /// <summary>Namespace scope for the squash (drives topology capture + snapshot scope).</summary>
    public string Namespace { get; }

    /// <summary>
    /// Captures a snapshot of an ephemeral Aerospike cluster after applying
    /// the supplied migration version range. Callers (CLI, test harness)
    /// inject a concrete implementation; production uses Testcontainers
    /// Aerospike + <c>Info.Request("sets/&lt;ns&gt;", "sindex/&lt;ns&gt;")</c>.
    /// </summary>
    public Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> CaptureSnapshotAsync { get; }

    public AerospikeSquashGenerationContext(
        string squashName,
        long squashVersion,
        IAerospikeClient client,
        string @namespace,
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> captureSnapshotAsync )
    {
        if ( string.IsNullOrWhiteSpace( squashName ) )
            throw new ArgumentException( "squashName is required.", nameof( squashName ) );
        if ( squashVersion <= 0 )
            throw new ArgumentException( "squashVersion must be positive.", nameof( squashVersion ) );
        if ( string.IsNullOrWhiteSpace( @namespace ) )
            throw new ArgumentException( "namespace is required.", nameof( @namespace ) );

        SquashName = squashName;
        SquashVersion = squashVersion;
        Client = client ?? throw new ArgumentNullException( nameof( client ) );
        Namespace = @namespace;
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
    AerospikeTopologySignature RequiredTopology );

/// <summary>
/// Result of a single snapshot capture. <see cref="SnapshotBlob"/> is the raw
/// <c>[sets]</c>/<c>[sindex]</c> section-headered blob produced by the capture
/// function; the canonicalizer normalizes it downstream.
/// </summary>
public sealed record SnapshotCaptureResult(
    string SnapshotBlob );
