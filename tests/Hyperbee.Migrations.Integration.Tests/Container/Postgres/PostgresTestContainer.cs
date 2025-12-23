using DotNet.Testcontainers.Builders;
using Testcontainers.PostgreSql;

namespace Hyperbee.Migrations.Integration.Tests.Container.Postgres;

public class PostgresTestContainer
{
    public static IDbConnection Connection { get; set; }
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

        var postgresContainer = new PostgreSqlBuilder()
            .WithNetwork( network )
            .WithNetworkAliases( "db" )
            .WithDatabase( "postgres" )
            .WithUsername( "test" )
            .WithPassword( "test" )
            .WithPortBinding( containerPort: 5432, hostPort: 6543 )
            .WithWaitStrategy( DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable( 5432 ) )
            .WithCleanUp( true )
            .Build();

        await postgresContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        Connection = new Npgsql.NpgsqlConnection( postgresContainer.GetConnectionString() + ";Include Error Detail=true" );
        Network = network;

    }
}
