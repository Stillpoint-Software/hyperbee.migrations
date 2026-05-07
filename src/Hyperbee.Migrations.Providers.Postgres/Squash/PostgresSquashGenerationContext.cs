using Hyperbee.Migrations.Squash;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Postgres-specific squash generation context. Carries the live datasource
/// (used for topology capture from the operator's ledger DB) plus a
/// delegate-injected snapshot capture mechanism.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot capture is parameterized so the runtime library doesn't need a
/// hard Testcontainers dependency. The CLI tool (Phase 8) and integration
/// test suite wire concrete capture functions; production code wires a
/// Testcontainers + <c>docker exec pg_dump</c> implementation.
/// </para>
/// <para>
/// The capture function receives the migration assembly metadata it needs to
/// apply migrations into an ephemeral Postgres of the right server-major
/// version (per ADR-0019 A10), and returns the canonical schema dump as a
/// string.
/// </para>
/// </remarks>
public sealed class PostgresSquashGenerationContext : ISquashGenerationContext
{
    public string ProviderId => PostgresTopologySignature.ProviderIdValue;
    public string SquashName { get; }
    public long SquashVersion { get; }

    /// <summary>Live data source for the operator's ledger DB.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>
    /// Captures a snapshot of an ephemeral Postgres after applying the supplied
    /// migration version range. Callers (CLI, test harness) inject a concrete
    /// implementation; production uses Testcontainers + <c>pg_dump --schema-only</c>.
    /// </summary>
    public Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> CaptureSnapshotAsync { get; }

    public PostgresSquashGenerationContext(
        string squashName,
        long squashVersion,
        NpgsqlDataSource dataSource,
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> captureSnapshotAsync )
    {
        if ( string.IsNullOrWhiteSpace( squashName ) )
            throw new ArgumentException( "squashName is required.", nameof( squashName ) );
        if ( squashVersion <= 0 )
            throw new ArgumentException( "squashVersion must be positive.", nameof( squashVersion ) );

        SquashName = squashName;
        SquashVersion = squashVersion;
        DataSource = dataSource ?? throw new ArgumentNullException( nameof( dataSource ) );
        CaptureSnapshotAsync = captureSnapshotAsync ?? throw new ArgumentNullException( nameof( captureSnapshotAsync ) );
    }
}

/// <summary>
/// Inputs for a single snapshot capture round. Identifies the version range
/// to apply (inclusive) and the topology the ephemeral container must match.
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
    PostgresTopologySignature RequiredTopology );

/// <summary>
/// Result of a single snapshot capture. <see cref="DumpText"/> is the raw
/// <c>pg_dump --schema-only</c> output; the canonicalizer normalizes it
/// downstream. <see cref="SequenceLastValues"/> carries the
/// <c>SELECT last_value</c> readings used to emit a <c>setval(...)</c>
/// post-block in the generated squash.
/// </summary>
public sealed record SnapshotCaptureResult(
    string DumpText,
    IReadOnlyDictionary<string, long> SequenceLastValues );
