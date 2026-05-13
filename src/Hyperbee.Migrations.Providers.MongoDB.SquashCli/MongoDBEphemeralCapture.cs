using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.SquashCli;

/// <summary>
/// MongoDB snapshot capture orchestrator. Consumes an
/// <see cref="IEphemeralProvisioner"/> for the container lifecycle; applies
/// migrations through a caller-supplied delegate; captures via
/// <see cref="MongoDBSnapshotCapture"/>.
/// </summary>
public sealed class MongoDBEphemeralCapture : IAsyncDisposable
{
    private readonly IEphemeralProvisioner _provisioner;
    private readonly Func<string, long, CancellationToken, Task> _applyMigrations;

    public MongoDBEphemeralCapture(
        Func<string, long, CancellationToken, Task> applyMigrations,
        IEphemeralProvisioner provisioner = null )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
        _provisioner = provisioner ?? new MongoDBEphemeralProvisioner();
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string databaseName,
        string image,
        CancellationToken cancellationToken )
    {
        var hints = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        if ( !string.IsNullOrWhiteSpace( image ) )
            hints["image"] = image;

        await using var rawFixture = await _provisioner.ProvisionAsync( hints, cancellationToken )
            .ConfigureAwait( false );

        await _applyMigrations( rawFixture.ConnectionString, request.UpToVersion, cancellationToken )
            .ConfigureAwait( false );

        var client = new MongoClient( rawFixture.ConnectionString );
        var blob = await MongoDBSnapshotCapture.CaptureAsync( client, databaseName, cancellationToken )
            .ConfigureAwait( false );

        return new SnapshotCaptureResult( blob );
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
