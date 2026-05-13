using Aerospike.Client;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;

namespace Hyperbee.Migrations.Providers.Aerospike.SquashCli;

/// <summary>
/// Aerospike snapshot capture orchestrator. Consumes an
/// <see cref="IEphemeralProvisioner"/> for the container lifecycle; applies
/// migrations through a caller-supplied delegate; captures the section-
/// headered snapshot via <see cref="AerospikeSnapshotCapture"/>.
/// </summary>
public sealed class AerospikeEphemeralCapture : IAsyncDisposable
{
    private readonly IEphemeralProvisioner _provisioner;
    private readonly Func<string, int, long, CancellationToken, Task> _applyMigrations;

    public AerospikeEphemeralCapture(
        Func<string, int, long, CancellationToken, Task> applyMigrations,
        IEphemeralProvisioner provisioner = null )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
        _provisioner = provisioner ?? new AerospikeEphemeralProvisioner();
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string @namespace,
        CancellationToken cancellationToken )
    {
        var hints = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

        await using var rawFixture = await _provisioner.ProvisionAsync( hints, cancellationToken )
            .ConfigureAwait( false );

        if ( rawFixture is not AerospikeEphemeralFixture asFixture )
        {
            throw new InvalidOperationException(
                $"IEphemeralProvisioner returned `{rawFixture?.GetType().FullName}`; " +
                $"AerospikeEphemeralCapture requires `{nameof( AerospikeEphemeralFixture )}` " +
                "(or a derived type) so the snapshot pipeline can resolve the host/port." );
        }

        await _applyMigrations( asFixture.Host, asFixture.Port, request.UpToVersion, cancellationToken )
            .ConfigureAwait( false );

        using var client = new AerospikeClient( asFixture.Host, asFixture.Port );
        var blob = await AerospikeSnapshotCapture.CaptureAsync( client, @namespace, cancellationToken )
            .ConfigureAwait( false );

        return new SnapshotCaptureResult( blob );
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
