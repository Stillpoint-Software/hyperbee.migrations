#nullable enable
using Hyperbee.Migrations.Squash;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// OpenSearch-specific squash generation context. Carries the live cluster
/// handle plus a delegate-injected snapshot capture mechanism.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot capture is parameterized so the runtime library does not need a
/// hard Testcontainers dependency. The CLI tool and integration test suite
/// wire concrete capture functions; production wires a Testcontainers
/// OpenSearch instance against which the migration range is applied before
/// the REST probes that populate the snapshot blob.
/// </para>
/// <para>
/// The capture function receives the migration metadata it needs (label +
/// upper-bound version + required topology axes per ADR-0019 A10) and returns
/// the section-headered snapshot blob ready for
/// <see cref="OpenSearchSnapshotCanonicalizer"/>.
/// </para>
/// </remarks>
public sealed class OpenSearchSquashGenerationContext : ISquashGenerationContext
{
    public string ProviderId => OpenSearchTopologySignature.ProviderIdValue;
    public string SquashName { get; }
    public long SquashVersion { get; }

    /// <summary>Live OpenSearch client for the operator's cluster.</summary>
    public IOpenSearchClient Client { get; }

    /// <summary>
    /// Captures a snapshot of an ephemeral OpenSearch cluster after applying
    /// the supplied migration version range. Callers (CLI, test harness)
    /// inject a concrete implementation; production uses Testcontainers
    /// OpenSearch + REST probes (delegated to
    /// <see cref="OpenSearchSnapshotCapture.CaptureAsync"/>).
    /// </summary>
    public Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> CaptureSnapshotAsync { get; }

    public OpenSearchSquashGenerationContext(
        string squashName,
        long squashVersion,
        IOpenSearchClient client,
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> captureSnapshotAsync )
    {
        if ( string.IsNullOrWhiteSpace( squashName ) )
            throw new ArgumentException( "squashName is required.", nameof( squashName ) );
        if ( squashVersion <= 0 )
            throw new ArgumentException( "squashVersion must be positive.", nameof( squashVersion ) );

        SquashName = squashName;
        SquashVersion = squashVersion;
        Client = client ?? throw new ArgumentNullException( nameof( client ) );
        CaptureSnapshotAsync = captureSnapshotAsync ?? throw new ArgumentNullException( nameof( captureSnapshotAsync ) );
    }
}

/// <summary>
/// Inputs for a single snapshot capture round. Identifies the version range
/// to apply (inclusive) and the topology the ephemeral cluster must match.
/// </summary>
public sealed record SnapshotCaptureRequest(
    string Label,
    long UpToVersion,
    OpenSearchTopologySignature RequiredTopology );

/// <summary>
/// Result of a single snapshot capture. <see cref="SnapshotBlob"/> is the
/// section-headered blob produced by the capture function; the canonicalizer
/// normalizes it downstream.
/// </summary>
public sealed record SnapshotCaptureResult(
    string SnapshotBlob );
