using Couchbase;
using Hyperbee.Migrations.Squash.Cli;
using Testcontainers.Couchbase;

namespace Hyperbee.Migrations.Providers.Couchbase.SquashCli;

/// <summary>
/// Default Couchbase <see cref="IEphemeralProvisioner"/>: spins an ephemeral
/// Couchbase Server container via <see cref="CouchbaseBuilder"/> and uses
/// <c>?network=external</c> on the resulting connection string so the
/// host-side SDK resolves the cluster map through the host-bound ports.
/// Suitable for CLI invocations from the operator's workstation.
/// </summary>
public sealed class CouchbaseEphemeralProvisioner : IEphemeralProvisioner
{
    public async Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken )
    {
        _ = hints; // currently no Couchbase-specific hints; image override TBD
        var container = new CouchbaseBuilder()
            .WithCleanUp( true )
            .Build();

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );

        var connectionString = container.GetConnectionString() + "?network=external";
        var mgmtPort = container.GetMappedPublicPort( CouchbaseBuilder.MgmtPort );

        return new CouchbaseEphemeralFixture(
            container,
            connectionString,
            container.Hostname,
            mgmtPort,
            CouchbaseBuilder.DefaultUsername,
            CouchbaseBuilder.DefaultPassword );
    }
}

/// <summary>
/// Couchbase-specific fixture exposing the management port + credentials
/// required by <see cref="CouchbaseSnapshotCapture"/>'s REST API hook.
/// Disposal tears down the container.
/// </summary>
public sealed class CouchbaseEphemeralFixture : IEphemeralFixture
{
    private readonly CouchbaseContainer _container;

    public string Hostname { get; }
    public int MgmtPort { get; }
    public string Username { get; }
    public string Password { get; }

    public string ConnectionString { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal CouchbaseEphemeralFixture(
        CouchbaseContainer container,
        string connectionString,
        string hostname,
        int mgmtPort,
        string username,
        string password )
    {
        _container = container;
        ConnectionString = connectionString;
        Hostname = hostname;
        MgmtPort = mgmtPort;
        Username = username;
        Password = password;
        Metadata = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["hostname"] = hostname,
            ["mgmt-port"] = mgmtPort.ToString( System.Globalization.CultureInfo.InvariantCulture ),
            ["username"] = username
        };
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// Sibling-container variant of <see cref="CouchbaseEphemeralProvisioner"/>
/// for the case where the CLI itself runs INSIDE a Docker container
/// (CI pipelines, containerized operator tooling). The host-side
/// <c>?network=external</c> trick does not apply when the CLI is in the
/// same Docker network; instead the SDK reaches the Couchbase container
/// through the Docker network alias.
/// </summary>
/// <remarks>
/// <para>
/// The fixture connects via <c>couchbase://&lt;alias&gt;</c> (default alias
/// <c>db</c>; override via the <c>network-alias</c> hint). Suitable when
/// the CLI process is itself a container peered with the Couchbase
/// container on a shared <see cref="DotNet.Testcontainers.Networks.INetwork"/>.
/// </para>
/// <para>
/// Operators using this variant supply a pre-existing network via the
/// <c>network-name</c> hint OR let the provisioner create a transient
/// network per invocation. The transient-network path keeps the
/// abstraction self-contained.
/// </para>
/// </remarks>
public sealed class CouchbaseSiblingContainerProvisioner : IEphemeralProvisioner
{
    public async Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken )
    {
        var alias = hints != null && hints.TryGetValue( "network-alias", out var a ) && !string.IsNullOrWhiteSpace( a )
            ? a
            : "db";

        var networkName = hints != null && hints.TryGetValue( "network-name", out var n ) && !string.IsNullOrWhiteSpace( n )
            ? n
            : "hyperbee-squash-" + Guid.NewGuid().ToString( "N" )[..12];

        var network = new DotNet.Testcontainers.Builders.NetworkBuilder()
            .WithName( networkName )
            .WithCleanUp( true )
            .Build();
        await network.CreateAsync( cancellationToken ).ConfigureAwait( false );

        var container = new CouchbaseBuilder()
            .WithCleanUp( true )
            .WithNetwork( network )
            .WithNetworkAliases( alias )
            .Build();

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );

        // Inside the Docker network, the SDK reaches the cluster via the
        // alias on the default couchbase ports (8091 + 11210). No
        // network=external here -- the CLI process is presumed to share
        // this network.
        var connectionString = $"couchbase://{alias}";
        var mgmtPort = container.GetMappedPublicPort( CouchbaseBuilder.MgmtPort );

        return new CouchbaseSiblingContainerFixture(
            container,
            network,
            connectionString,
            alias,
            mgmtPort,
            CouchbaseBuilder.DefaultUsername,
            CouchbaseBuilder.DefaultPassword );
    }
}

internal sealed class CouchbaseSiblingContainerFixture : IEphemeralFixture
{
    private readonly CouchbaseContainer _container;
    private readonly DotNet.Testcontainers.Networks.INetwork _network;

    public string ConnectionString { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public CouchbaseSiblingContainerFixture(
        CouchbaseContainer container,
        DotNet.Testcontainers.Networks.INetwork network,
        string connectionString,
        string alias,
        int mgmtPort,
        string username,
        string password )
    {
        _container = container;
        _network = network;
        ConnectionString = connectionString;
        Metadata = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["network-alias"] = alias,
            ["mgmt-port"] = mgmtPort.ToString( System.Globalization.CultureInfo.InvariantCulture ),
            ["username"] = username
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait( false );
        await _network.DisposeAsync().ConfigureAwait( false );
    }
}
