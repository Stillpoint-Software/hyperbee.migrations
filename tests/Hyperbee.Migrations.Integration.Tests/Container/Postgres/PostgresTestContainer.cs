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

        // No WaitStrategy override -- use the Testcontainers.PostgreSql
        // default, which runs `pg_isready` until success. The previous
        // UntilExternalTcpPortIsAvailable check is weaker than pg_isready:
        // Postgres opens its TCP port early in startup (before initdb's
        // postgres role + database creation completes), so a connect via
        // the TCP-port check can succeed and immediately get "Connection
        // reset by peer" during the SCRAM handshake. On a heavily loaded
        // CI runner the gap is wide enough to surface as test flakiness;
        // pg_isready waits for postgres to actually accept logins.
        var postgresContainer = new PostgreSqlBuilder()
            .WithNetwork( network )
            .WithNetworkAliases( "db" )
            .WithDatabase( "postgres" )
            .WithUsername( "test" )
            .WithPassword( "test" )
            .WithPortBinding( containerPort: 5432, hostPort: 6543 )
            .WithCleanUp( true )
            .Build();

        await postgresContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        Connection = new Npgsql.NpgsqlConnection( postgresContainer.GetConnectionString() + ";Include Error Detail=true" );
        Network = network;

    }
}
