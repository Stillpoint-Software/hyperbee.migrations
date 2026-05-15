using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// Default OpenSearch <see cref="IEphemeralProvisioner"/>: spins an ephemeral
/// <c>opensearchproject/opensearch:&lt;tag&gt;</c> container via the generic
/// <see cref="ContainerBuilder"/>. Reads <c>image</c> from hints; defaults to
/// <c>opensearchproject/opensearch:2.18.0</c>.
/// </summary>
public sealed class OpenSearchEphemeralProvisioner : IEphemeralProvisioner
{
    private const int InternalPort = 9200;

    public async Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken )
    {
        var image = hints != null && hints.TryGetValue( "image", out var img ) && !string.IsNullOrWhiteSpace( img )
            ? img
            : "opensearchproject/opensearch:2.18.0";

        var container = new ContainerBuilder( image )
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

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );

        var endpoint = new UriBuilder(
            "http",
            container.Hostname,
            container.GetMappedPublicPort( InternalPort ) ).Uri;

        return new OpenSearchEphemeralFixture( container, endpoint );
    }
}

/// <summary>
/// OpenSearch-specific fixture exposing the resolved <see cref="Endpoint"/>
/// URI. Disposal tears down the container.
/// </summary>
public sealed class OpenSearchEphemeralFixture : IEphemeralFixture
{
    private readonly IContainer _container;

    public Uri Endpoint { get; }
    public string ConnectionString { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal OpenSearchEphemeralFixture( IContainer container, Uri endpoint )
    {
        _container = container;
        Endpoint = endpoint;
        ConnectionString = endpoint.ToString();
        Metadata = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["endpoint"] = endpoint.ToString()
        };
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
