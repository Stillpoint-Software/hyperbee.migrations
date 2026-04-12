using Aerospike.Client;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Hyperbee.Migrations.Integration.Tests.Container.Aerospike;

public class AerospikeTestContainer
{
    public static IAsyncClient AsyncClient { get; set; }
    public static IAerospikeClient Client { get; set; }
    public static INetwork Network { get; set; }
    public static string Host { get; set; }
    public static int Port { get; set; }

    public static async Task Initialize( TestContext context )
    {
        var cancellationToken = context.CancellationTokenSource.Token;

        var network = new NetworkBuilder()
            .WithName( Guid.NewGuid().ToString( "D" ) )
            .WithCleanUp( true )
            .Build();

        await network.CreateAsync( cancellationToken )
            .ConfigureAwait( false );

        var aerospikeContainer = new ContainerBuilder()
            .WithImage( "aerospike/aerospike-server:latest" )
            .WithNetwork( network )
            .WithNetworkAliases( "db" )
            .WithPortBinding( 3100, 3000 )
            .WithCleanUp( true )
            .WithWaitStrategy( DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable( 3000 ) )
            .Build();

        await aerospikeContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        Host = aerospikeContainer.Hostname;
        Port = aerospikeContainer.GetMappedPublicPort( 3000 );

        var asyncClient = new AsyncClient( Host, Port );
        AsyncClient = asyncClient;
        Client = asyncClient;
        Network = network;
    }
}
