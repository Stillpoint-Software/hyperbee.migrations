using Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Couchbase snapshot capture orchestrator. Consumes an
/// <see cref="IEphemeralProvisioner"/> for the container lifecycle (default
/// <see cref="CouchbaseEphemeralProvisioner"/>; sibling-container variant via
/// <see cref="CouchbaseSiblingContainerProvisioner"/>); applies migrations
/// through a caller-supplied delegate; captures via
/// <see cref="CouchbaseSnapshotCapture"/>.
/// </summary>
public sealed class CouchbaseEphemeralCapture : IAsyncDisposable
{
    private readonly IEphemeralProvisioner _provisioner;
    private readonly Func<string, long, IReadOnlyDictionary<string, string>, CancellationToken, Task> _applyMigrations;

    public CouchbaseEphemeralCapture(
        Func<string, long, IReadOnlyDictionary<string, string>, CancellationToken, Task> applyMigrations,
        IEphemeralProvisioner provisioner = null )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
        _provisioner = provisioner ?? new CouchbaseEphemeralProvisioner();
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string bucketName,
        CancellationToken cancellationToken )
    {
        var hints = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

        await using var rawFixture = await _provisioner.ProvisionAsync( hints, cancellationToken )
            .ConfigureAwait( false );

        // Capture needs hostname + mgmt-port + credentials. The default
        // CouchbaseEphemeralFixture carries them as typed properties; the
        // sibling-container fixture exposes them via Metadata. Read both
        // paths so either provisioner shape works.
        string username, password, mgmtHost;
        int mgmtPort;

        if ( rawFixture is CouchbaseEphemeralFixture defaultFixture )
        {
            username = defaultFixture.Username;
            password = defaultFixture.Password;
            mgmtHost = defaultFixture.Hostname;
            mgmtPort = defaultFixture.MgmtPort;
        }
        else
        {
            username = ReadMetadata( rawFixture, "username" )
                ?? throw new InvalidOperationException( "Couchbase fixture metadata missing `username`." );
            mgmtPort = int.Parse(
                ReadMetadata( rawFixture, "mgmt-port" )
                    ?? throw new InvalidOperationException( "Couchbase fixture metadata missing `mgmt-port`." ),
                System.Globalization.CultureInfo.InvariantCulture );
            mgmtHost = ReadMetadata( rawFixture, "hostname" )
                ?? ReadMetadata( rawFixture, "network-alias" )
                ?? throw new InvalidOperationException( "Couchbase fixture metadata missing `hostname` or `network-alias`." );
            password = ReadMetadata( rawFixture, "password" ) ?? "password";
        }

        // Hand the mapped mgmt port to the apply callback so the host's
        // ClusterOptions.BootstrapHttpPort can be set correctly. The
        // ephemeral container's mgmt port is randomly assigned by Docker;
        // without this the host's CouchbaseRestApiService defaults to
        // 8091 and 404s on /pools/default/buckets/<name>.
        var hostHints = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["mgmt-port"] = mgmtPort.ToString( System.Globalization.CultureInfo.InvariantCulture ),
            ["mgmt-host"] = mgmtHost
        };

        await _applyMigrations( rawFixture.ConnectionString, request.UpToVersion, hostHints, cancellationToken )
            .ConfigureAwait( false );

        var clusterOptions = new ClusterOptions
        {
            ConnectionString = rawFixture.ConnectionString,
            UserName = username,
            Password = password,
            BootstrapHttpPort = mgmtPort
        };

        var cluster = await Cluster.ConnectAsync( clusterOptions ).ConfigureAwait( false );
        try
        {
            await cluster.WaitUntilReadyAsync( TimeSpan.FromMinutes( 1 ) ).ConfigureAwait( false );

            using var http = new HttpClient
            {
                BaseAddress = new Uri( $"http://{mgmtHost}:{mgmtPort}" )
            };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String( System.Text.Encoding.ASCII.GetBytes( $"{username}:{password}" ) ) );

            var restApi = new CouchbaseRestApiService(
                http,
                new OptionsWrapper<ClusterOptions>( clusterOptions ),
                NullLogger<CouchbaseRestApiService>.Instance );

            var blob = await CouchbaseSnapshotCapture.CaptureAsync(
                cluster, restApi, bucketName, cancellationToken ).ConfigureAwait( false );

            return new SnapshotCaptureResult( blob );
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait( false );
        }
    }

    private static string ReadMetadata( IEphemeralFixture fixture, string key )
        => fixture.Metadata != null && fixture.Metadata.TryGetValue( key, out var v ) && !string.IsNullOrWhiteSpace( v )
            ? v
            : null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
