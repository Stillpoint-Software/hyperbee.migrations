using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.SquashCli;

/// <summary>
/// OpenSearch snapshot capture orchestrator. Consumes an
/// <see cref="IEphemeralProvisioner"/> for the container lifecycle; applies
/// migrations through a caller-supplied delegate; captures via
/// <see cref="OpenSearchSnapshotCapture"/>.
/// </summary>
public sealed class OpenSearchEphemeralCapture : IAsyncDisposable
{
    private readonly IEphemeralProvisioner _provisioner;
    private readonly Func<Uri, long, CancellationToken, Task> _applyMigrations;

    public OpenSearchEphemeralCapture(
        Func<Uri, long, CancellationToken, Task> applyMigrations,
        IEphemeralProvisioner provisioner = null )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
        _provisioner = provisioner ?? new OpenSearchEphemeralProvisioner();
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string image,
        CancellationToken cancellationToken )
    {
        var hints = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        if ( !string.IsNullOrWhiteSpace( image ) )
            hints["image"] = image;

        await using var rawFixture = await _provisioner.ProvisionAsync( hints, cancellationToken )
            .ConfigureAwait( false );

        if ( rawFixture is not OpenSearchEphemeralFixture osFixture )
        {
            throw new InvalidOperationException(
                $"IEphemeralProvisioner returned `{rawFixture?.GetType().FullName}`; " +
                $"OpenSearchEphemeralCapture requires `{nameof( OpenSearchEphemeralFixture )}` " +
                "(or a derived type) so the snapshot pipeline can resolve the endpoint URI." );
        }

        await _applyMigrations( osFixture.Endpoint, request.UpToVersion, cancellationToken )
            .ConfigureAwait( false );

        var settings = new ConnectionSettings( osFixture.Endpoint )
            .DefaultIndex( "migrations" )
            .ThrowExceptions();
        var client = new OpenSearchClient( settings );
        const string ismPathPrefix = "_plugins/_ism";
        var blob = await OpenSearchSnapshotCapture.CaptureAsync( client, ismPathPrefix, cancellationToken )
            .ConfigureAwait( false );

        return new SnapshotCaptureResult( blob );
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
