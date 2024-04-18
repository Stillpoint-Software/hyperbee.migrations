using DotNet.Testcontainers.Networks;
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
        Connection = InitializeTestContainer.Connection;
        Network = InitializeTestContainer.Network;
    }

    // [TestMethod]
    // public async Task Should_Run_WithPostgres()
    // {
    //     await MigrationContainer.RunMigrationsAsync( Connection, Network );
    // }

    [TestMethod]
    public async Task Should_Succeed_WhenRunningUpTwice()
    {
        await MigrationContainer.RunMigrationsAsync( Connection, Network );

        await MigrationContainer.RunMigrationsAsync( Connection, Network );

        // TODO: Assert no migrations ran on second run
    }

    // [TestMethod]
    // public async Task Should_Fail_WhenMigrationHasLock()
    // {
    //     // TODO: Need a way to verify locks
    //     var migration1 = MigrationContainer.RunMigrationsAsync( Connection, Network );
    //     var migration2 = MigrationContainer.RunMigrationsAsync( Connection, Network );
    //
    //     await Task.WhenAll( migration1, migration2 );
    // }
}