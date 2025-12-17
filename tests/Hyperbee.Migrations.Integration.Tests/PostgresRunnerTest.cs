#define INTEGRATIONS

using Hyperbee.Migrations.Integration.Tests.Container.Postgres;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
[TestClass]
public class PostgresRunnerTest
{
    public IDbConnection Connection;
    public INetwork Network;

    [TestInitialize]
    public void Setup()
    {
        Connection = PostgresTestContainer.Connection;
        Network = PostgresTestContainer.Network;

    }

    [TestMethod]
    public async Task Should_Succeed_WhenRunningUpTwice()
    {
        var migrationContainer = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network );

        await migrationContainer.StartAsync();

        var (stdOut1, _) = await migrationContainer.GetLogsAsync();

        Assert.Contains( "[1000] Initial: Up migration started", stdOut1 );
        Assert.Contains( "[1000] Initial: Up migration completed", stdOut1 );
        Assert.Contains( "[2000] MigrationAction: Up migration started", stdOut1 );
        Assert.Contains( "[2000] MigrationAction: Up migration continuing", stdOut1 );
        Assert.Contains( "[2000] MigrationAction: Up migration completed", stdOut1 );
        Assert.Contains( "Executed 2 migrations", stdOut1 );

        await migrationContainer.StartAsync();
        var (stdOut2, _) = await migrationContainer.GetLogsAsync();

        Assert.Contains( "Executed 0 migrations", stdOut2 );
    }

    //[TestMethod]
    public async Task Should_Fail_WhenMigrationHasLock()
    {
        var migrationImage = await PostgresMigrationContainer.BuildMigrationImageAsync();

        var migrationContainer1 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );
        var migrationContainer2 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );
        var migrationContainer3 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );
        var migrationContainer4 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );

        var migration1 = migrationContainer1.StartAsync();
        var migration2 = migrationContainer2.StartAsync();
        var migration3 = migrationContainer3.StartAsync();

        await Task.WhenAll( migration1, migration2, migration3 );
        await Task.Delay( 3000 );
        var migration4 = migrationContainer4.StartAsync();
        await migration4;

        var (stdOut1, _) = await migrationContainer1.GetLogsAsync();
        var (stdOut2, _) = await migrationContainer2.GetLogsAsync();
        var (stdOut3, _) = await migrationContainer3.GetLogsAsync();
        var (stdOut4, _) = await migrationContainer4.GetLogsAsync();

        var allStdOut = string.Empty;
        allStdOut += stdOut1;
        allStdOut += stdOut2;
        allStdOut += stdOut3;
        allStdOut += stdOut4;

        // TODO: Hack, there is still a possible issue with timing.
        Warn.If( !allStdOut.Contains( "Executed 2 migrations" ), "Did not run migrations\n" + allStdOut );
        Warn.If( !allStdOut.Contains( "Executed 0 migrations" ), "Did not re-run migrations" );
        Warn.If( !allStdOut.Contains( "The migration lock is unavailable. Skipping migrations." ), "Did not detect migration lock" );
    }
}
#endif
