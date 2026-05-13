using DotNet.Testcontainers.Builders;
using Testcontainers.MongoDb;

namespace Hyperbee.Migrations.Integration.Tests.Container.MongoDb;

public class MongoDbTestContainer
{
    public static IMongoClient Client { get; set; }
    public static string ConnectionString { get; set; }
    public static INetwork Network { get; set; }

    public static async Task Initialize( TestContext context )
    {
        // TODO: clean up
        //  - Allow for configuration of ports and settings
        //  - Create IDbConnection cleanly with DI helpers

        var cancellationToken = context.CancellationTokenSource.Token;

        var network = new NetworkBuilder()
            .WithName( Guid.NewGuid().ToString( "D" ) )
            .WithCleanUp( true )
            .Build();

        await network.CreateAsync( cancellationToken )
            .ConfigureAwait( false );

        // Mapped public port (not a fixed 28017 binding). Fixed bindings on
        // Windows/WSL2 get retained by HNS after Docker container teardown
        // and surface as "port is already allocated" on the next test run.
        // Mapped ports are allocated fresh per container and avoid the
        // retention path entirely; downstream consumers read via
        // MongoDbTestContainer.ConnectionString rather than assuming a
        // fixed host:port.
        var mongoDbContainer = new MongoDbBuilder()
            .WithNetwork( network )
            .WithNetworkAliases( "db" )
            .WithUsername( "test" )
            .WithPassword( "test" )
            .WithCleanUp( true )
            .WithWaitStrategy( DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable( 27017 ) )
            .Build();

        await mongoDbContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        ConnectionString = mongoDbContainer.GetConnectionString();
        Client = new MongoClient( ConnectionString );
        Network = network;
    }
}
