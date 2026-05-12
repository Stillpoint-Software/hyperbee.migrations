using Aerospike.Client;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Aerospike.SquashCli;

/// <summary>
/// Aerospike snapshot capture concrete: spins an ephemeral aerospike-server
/// container, applies migrations through the requested upper bound via the
/// caller-supplied delegate, then captures the section-headered snapshot via
/// <see cref="AerospikeSnapshotCapture"/>. Per ADR-0019 A18 the container
/// is torn down after each capture.
/// </summary>
public sealed class AerospikeEphemeralCapture : IAsyncDisposable
{
    private const int InternalPort = 3000;

    private readonly Func<string, int, long, CancellationToken, Task> _applyMigrations;

    /// <param name="applyMigrations">
    /// Caller-supplied callback that applies the operator's migration
    /// assembly through the requested upper version. Takes (host, mappedPort,
    /// upToVersion, ct). Typically wraps the discovered IMigrationHost's
    /// ConfigureAsync against an Aerospike client bound to the ephemeral
    /// container.
    /// </param>
    public AerospikeEphemeralCapture(
        Func<string, int, long, CancellationToken, Task> applyMigrations )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string @namespace,
        CancellationToken cancellationToken )
    {
        // R-15 ADR-0019 A10: server-version-matched container per the
        // topology axes. Aerospike's topology surfaces server-major /
        // server-edition / cluster-size; for now the CLI uses the "latest"
        // tag because the canonicalizer scopes drift detection to the
        // [sets] / [sindex] feature surface and not server version per se.
        // A versioned tag swap is a v3.0.x follow-up if a determinism
        // regression surfaces.
        var image = "aerospike/aerospike-server:latest";

        var container = new ContainerBuilder( image )
            .WithPortBinding( InternalPort, assignRandomHostPort: true )
            // Same DEFAULT_TTL rationale as the test container fixture:
            // without nsup-period the migration lock record (expiration=60)
            // is rejected as FORBIDDEN_OP. 86400 = 24h is a benign cap.
            .WithEnvironment( "DEFAULT_TTL", "86400" )
            .WithCleanUp( true )
            .WithWaitStrategy(
                DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable( InternalPort ) )
            .Build();

        try
        {
            await container.StartAsync( cancellationToken ).ConfigureAwait( false );

            var host = container.Hostname;
            var port = container.GetMappedPublicPort( InternalPort );

            await _applyMigrations( host, port, request.UpToVersion, cancellationToken ).ConfigureAwait( false );

            // Now connect a fresh client to the ephemeral container and run
            // the snapshot capture. Reuse the existing AerospikeSnapshotCapture
            // helper -- same code path the runtime library + integration tests
            // use, so the canonicalized output is byte-identical regardless of
            // who invokes it.
            using var client = new AerospikeClient( host, port );
            var blob = await AerospikeSnapshotCapture.CaptureAsync( client, @namespace, cancellationToken )
                .ConfigureAwait( false );

            return new SnapshotCaptureResult( blob );
        }
        finally
        {
            await container.DisposeAsync().ConfigureAwait( false );
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
