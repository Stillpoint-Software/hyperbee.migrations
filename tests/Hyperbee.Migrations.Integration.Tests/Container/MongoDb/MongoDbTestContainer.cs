using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Hyperbee.Migrations.Integration.Tests.Container.MongoDb;

public class MongoDbTestContainer
{
    public static IMongoClient Client { get; set; }
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

        var mongoDbContainer = new MongoDbBuilder()
            .WithNetwork( network )
            .WithNetworkAliases( "db" )
            .WithUsername( "test" )
            .WithPassword( "test" )
            .WithPortBinding( 28017, 27017 )
            .WithCleanUp( true )
            .WithWaitStrategy( Wait.ForUnixContainer().UntilPortIsAvailable( 27017 ) )
            .Build();

        await mongoDbContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        Client = new MongoClient( mongoDbContainer.GetConnectionString() );
        Network = network;
    }
}
