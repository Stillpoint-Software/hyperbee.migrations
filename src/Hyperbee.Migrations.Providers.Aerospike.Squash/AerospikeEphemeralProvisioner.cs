using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Default Aerospike <see cref="IEphemeralProvisioner"/>: spins an ephemeral
/// <c>aerospike/aerospike-server</c> container via the generic
/// <see cref="ContainerBuilder"/>. Reads <c>image</c> from hints; defaults to
/// <c>aerospike/aerospike-server:latest</c>.
/// </summary>
/// <remarks>
/// DEFAULT_TTL=86400 (24h) is required: without nsup-period the migration
/// runner's lock record (expiration=60) is rejected as FORBIDDEN_OP. Same
/// rationale as the test fixture in <c>AerospikeTestContainer</c>.
/// </remarks>
public sealed class AerospikeEphemeralProvisioner : IEphemeralProvisioner
{
    private const int InternalPort = 3000;

    public async Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken )
    {
        var image = hints != null && hints.TryGetValue( "image", out var img ) && !string.IsNullOrWhiteSpace( img )
            ? img
            : "aerospike/aerospike-server:latest";

        var container = new ContainerBuilder( image )
            .WithPortBinding( InternalPort, assignRandomHostPort: true )
            .WithEnvironment( "DEFAULT_TTL", "86400" )
            .WithCleanUp( true )
            .WithWaitStrategy(
                DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable( InternalPort ) )
            .Build();

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );
        return new AerospikeEphemeralFixture( container, container.Hostname, container.GetMappedPublicPort( InternalPort ) );
    }
}

/// <summary>
/// Aerospike-specific fixture exposing the host/port pair the AerospikeClient
/// uses. Disposal tears down the container.
/// </summary>
public sealed class AerospikeEphemeralFixture : IEphemeralFixture
{
    private readonly IContainer _container;

    public string Host { get; }
    public int Port { get; }
    public string ConnectionString { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal AerospikeEphemeralFixture( IContainer container, string host, int port )
    {
        _container = container;
        Host = host;
        Port = port;
        ConnectionString = $"{host}:{port}";
        Metadata = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["host"] = host,
            ["port"] = port.ToString( System.Globalization.CultureInfo.InvariantCulture )
        };
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
