using Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Couchbase;

namespace Hyperbee.Migrations.Providers.Couchbase.SquashCli;

/// <summary>
/// Couchbase snapshot capture concrete: spins an ephemeral Couchbase Server
/// container via <see cref="CouchbaseBuilder"/>, applies migrations through
/// the requested upper bound, then captures via
/// <see cref="CouchbaseSnapshotCapture"/>. Per ADR-0019 A18 the container is
/// torn down after each capture.
/// </summary>
/// <remarks>
/// The host-side CLI talks to the containerized Couchbase via the bootstrap
/// connection string Testcontainers exposes. Cluster-map race vs. host SDK
/// is handled by passing `network=external` in the connection settings -- this
/// instructs the SDK to use the externally-resolvable address it bootstrapped
/// from rather than the container-internal addresses the cluster reports.
/// </remarks>
public sealed class CouchbaseEphemeralCapture : IAsyncDisposable
{
    private readonly Func<string, long, CancellationToken, Task> _applyMigrations;

    public CouchbaseEphemeralCapture(
        Func<string, long, CancellationToken, Task> applyMigrations )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string bucketName,
        CancellationToken cancellationToken )
    {
        // Testcontainers.Couchbase ships a CouchbaseBuilder that bootstraps
        // a single-node cluster with default services. The bucket the
        // operator's migrations target is created by their own IMigrationHost
        // setup (or by an InitializeAsync-time bucket-create per
        // CouchbaseRecordStore.InitializeAsync). The bucketName parameter
        // here just scopes the snapshot capture.
        _ = bucketName; // captured below; ContainerBuilder doesn't pre-create
        var container = new CouchbaseBuilder()
            .WithCleanUp( true )
            .Build();

        try
        {
            await container.StartAsync( cancellationToken ).ConfigureAwait( false );

            var connectionString = container.GetConnectionString() + "?network=external";

            await _applyMigrations( connectionString, request.UpToVersion, cancellationToken ).ConfigureAwait( false );

            // Now connect a fresh cluster handle for the capture phase. Use
            // the same external-network connection setting so cluster-map
            // resolution stays consistent.
            var clusterOptions = new ClusterOptions
            {
                ConnectionString = connectionString,
                UserName = CouchbaseBuilder.DefaultUsername,
                Password = CouchbaseBuilder.DefaultPassword
            };

            var cluster = await Cluster.ConnectAsync( clusterOptions ).ConfigureAwait( false );
            try
            {
                await cluster.WaitUntilReadyAsync( TimeSpan.FromMinutes( 1 ) ).ConfigureAwait( false );

                // Build the REST API service using the same credentials.
                // CouchbaseSnapshotCapture wants bucket/scope settings the
                // N1QL system tables don't expose, so it goes through REST.
                using var http = new HttpClient
                {
                    BaseAddress = new Uri( $"http://{container.Hostname}:{container.GetMappedPublicPort( CouchbaseBuilder.MgmtPort )}" )
                };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String( System.Text.Encoding.ASCII.GetBytes(
                            $"{CouchbaseBuilder.DefaultUsername}:{CouchbaseBuilder.DefaultPassword}" ) ) );

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
        finally
        {
            await container.DisposeAsync().ConfigureAwait( false );
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
