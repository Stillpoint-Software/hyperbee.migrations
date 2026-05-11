using Hyperbee.Migrations.Squash;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// MongoDB-specific squash generation context. Carries the live client +
/// database name plus a delegate-injected snapshot capture mechanism.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot capture is parameterized so the runtime library does not need
/// a hard Testcontainers dependency. The CLI tool and integration test
/// suite wire concrete capture functions; production wires a Testcontainers
/// MongoDB instance against which the migration range is applied before
/// the admin commands that populate the snapshot blob.
/// </para>
/// </remarks>
public sealed class MongoDBSquashGenerationContext : ISquashGenerationContext
{
    public string ProviderId => MongoDBTopologySignature.ProviderIdValue;
    public string SquashName { get; }
    public long SquashVersion { get; }

    /// <summary>Live MongoDB client for the operator's cluster.</summary>
    public IMongoClient Client { get; }

    /// <summary>Database scope for the squash (drives topology + snapshot scope).</summary>
    public string DatabaseName { get; }

    /// <summary>
    /// Captures a snapshot of an ephemeral MongoDB cluster after applying
    /// the supplied migration version range. Callers (CLI, test harness)
    /// inject a concrete implementation; production uses Testcontainers
    /// MongoDB + <see cref="MongoDBSnapshotCapture.CaptureAsync"/>.
    /// </summary>
    public Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> CaptureSnapshotAsync { get; }

    public MongoDBSquashGenerationContext(
        string squashName,
        long squashVersion,
        IMongoClient client,
        string databaseName,
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> captureSnapshotAsync )
    {
        if ( string.IsNullOrWhiteSpace( squashName ) )
            throw new ArgumentException( "squashName is required.", nameof( squashName ) );
        if ( squashVersion <= 0 )
            throw new ArgumentException( "squashVersion must be positive.", nameof( squashVersion ) );
        if ( string.IsNullOrWhiteSpace( databaseName ) )
            throw new ArgumentException( "databaseName is required.", nameof( databaseName ) );

        SquashName = squashName;
        SquashVersion = squashVersion;
        Client = client ?? throw new ArgumentNullException( nameof( client ) );
        DatabaseName = databaseName;
        CaptureSnapshotAsync = captureSnapshotAsync ?? throw new ArgumentNullException( nameof( captureSnapshotAsync ) );
    }
}

/// <summary>
/// Inputs for a single snapshot capture round.
/// </summary>
public sealed record SnapshotCaptureRequest(
    string Label,
    long UpToVersion,
    MongoDBTopologySignature RequiredTopology );

/// <summary>
/// Result of a single snapshot capture. <see cref="SnapshotBlob"/> is the
/// section-headered blob produced by the capture function.
/// </summary>
public sealed record SnapshotCaptureResult(
    string SnapshotBlob );
