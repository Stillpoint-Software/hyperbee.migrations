using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.SquashCli;

/// <summary>
/// OpenSearch snapshot capture concrete: spins an ephemeral
/// <c>opensearchproject/opensearch:&lt;version&gt;</c> container, applies
/// migrations through the requested upper bound via the caller-supplied
/// delegate, then captures the section-headered snapshot via
/// <see cref="OpenSearchSnapshotCapture"/>. Per ADR-0019 A18 the container
/// is torn down after each capture.
/// </summary>
public sealed class OpenSearchEphemeralCapture : IAsyncDisposable
{
    private const int InternalPort = 9200;

    private readonly Func<Uri, long, CancellationToken, Task> _applyMigrations;

    /// <param name="applyMigrations">
    /// Caller-supplied callback that applies the operator's migration
    /// assembly through the requested upper version. Takes (endpointUri,
    /// upToVersion, ct). Typically wraps the discovered IMigrationHost's
    /// ConfigureAsync against an OpenSearch client bound to the ephemeral
    /// container.
    /// </param>
    public OpenSearchEphemeralCapture(
        Func<Uri, long, CancellationToken, Task> applyMigrations )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string image,
        CancellationToken cancellationToken )
    {
        // Image is operator-supplied via provider-option `image` so the
        // ephemeral container's server-major matches the target topology
        // axis. Default mirrors the test container's pinned tag.
        var resolvedImage = string.IsNullOrWhiteSpace( image )
            ? "opensearchproject/opensearch:2.18.0"
            : image;

        var container = new ContainerBuilder( resolvedImage )
            .WithPortBinding( InternalPort, assignRandomHostPort: true )
            .WithEnvironment( "discovery.type", "single-node" )
            .WithEnvironment( "DISABLE_SECURITY_PLUGIN", "true" )
            .WithEnvironment( "DISABLE_INSTALL_DEMO_CONFIG", "true" )
            .WithEnvironment( "bootstrap.memory_lock", "false" )
            .WithEnvironment( "OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m" )
            .WithCleanUp( true )
            .WithWaitStrategy(
                DotNet.Testcontainers.Builders.Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded( req =>
                        req.ForPath( "/_cluster/health" )
                           .ForPort( InternalPort )
                           .ForStatusCode( System.Net.HttpStatusCode.OK ) ) )
            .Build();

        try
        {
            await container.StartAsync( cancellationToken ).ConfigureAwait( false );

            var endpoint = new UriBuilder(
                "http",
                container.Hostname,
                container.GetMappedPublicPort( InternalPort ) ).Uri;

            await _applyMigrations( endpoint, request.UpToVersion, cancellationToken ).ConfigureAwait( false );

            var settings = new ConnectionSettings( endpoint );
            var client = new OpenSearchClient( settings );
            // ISM prefix: modern endpoints use _plugins/_ism; the legacy
            // _opendistro/_ism is for older AWS-managed deployments.
            // Ephemeral fresh containers use modern by default.
            const string ismPathPrefix = "_plugins/_ism";
            var blob = await OpenSearchSnapshotCapture.CaptureAsync( client, ismPathPrefix, cancellationToken )
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
