using System.Data;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;

namespace Hyperbee.Migrations.Integration.Tests;

[TestClass]
public class InitializeTestContainer
{
    public static IDbConnection Connection { get; set; }
    public static INetwork Network { get; set; }


    [AssemblyInitialize]
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

        var postgresContainer = new PostgreSqlBuilder()
            .WithNetwork( network )
            .WithNetworkAliases( "db" )
            .WithDatabase( "postgres" )
            .WithUsername( "test" )
            .WithPassword( "test" )
            .WithPortBinding( 6543, 5432 )
            .WithCleanUp( true )
            .WithWaitStrategy( Wait.ForUnixContainer().UntilPortIsAvailable( 5432 ) )
            .Build();

        await postgresContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        Connection = new Npgsql.NpgsqlConnection( postgresContainer.GetConnectionString() + ";Include Error Detail=true" );
        Network = network;

    }
}