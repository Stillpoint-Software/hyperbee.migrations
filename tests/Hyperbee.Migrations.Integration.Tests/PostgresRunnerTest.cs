using DotNet.Testcontainers.Networks;
using Hyperbee.Migrations.Integration.Tests.Container.Postgres;
using System.Data;

namespace Hyperbee.Migrations.Integration.Tests;

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

    // [TestMethod]
    // public async Task Should_Run_WithPostgres()
    // {
    //     await MigrationContainer.RunMigrationsAsync( Connection, Network );
    // }

    [TestMethod]
    public async Task Should_Succeed_WhenRunningUpTwice()
    {
        var migrationContainer = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network );

        await migrationContainer.StartAsync( CancellationToken.None );
        await migrationContainer.StartAsync( CancellationToken.None );

        // TODO: Assert no migrations ran on second run
    }

    [TestMethod]
    public async Task Should_Fail_WhenMigrationHasLock()
    {
        var migrationImage = await PostgresMigrationContainer.BuildMigrationImageAsync();

        var migrationContainer1 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );
        var migrationContainer2 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );
        var migrationContainer3 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );
        var migrationContainer4 = await PostgresMigrationContainer.BuildMigrationsAsync( Connection, Network, migrationImage );

        var migration1 = migrationContainer1.StartAsync( CancellationToken.None );
        var migration2 = migrationContainer2.StartAsync( CancellationToken.None );
        var migration3 = migrationContainer3.StartAsync( CancellationToken.None );
        var migration4 = migrationContainer4.StartAsync( CancellationToken.None );

        await Task.WhenAll( migration1, migration2, migration3, migration4 );
    }
}