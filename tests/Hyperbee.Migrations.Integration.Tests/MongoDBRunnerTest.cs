#define INTEGRATIONS
using DotNet.Testcontainers.Networks;
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
[TestClass]
public class MongoDBRunnerTest
{
    public IMongoClient Client;
    public INetwork Network;

    [TestInitialize]
    public void Setup()
    {
        Client = MongoDbTestContainer.Client;
        Network = MongoDbTestContainer.Network;
    }

    [TestMethod]
    public async Task Should_Succeed_WhenRunningUpTwice()
    {
        var migrationContainer = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network );
        await migrationContainer.StartAsync();

        var (stdOut1, _) = await migrationContainer.GetLogsAsync();

        Assert.IsTrue( stdOut1.Contains( "[1000] Initial: Up migration started" ) );
        Assert.IsTrue( stdOut1.Contains( "[1000] Initial: Up migration completed" ) );
        Assert.IsTrue( stdOut1.Contains( "[2000] MigrationAction: Up migration started" ) );
        Assert.IsTrue( stdOut1.Contains( "[2000] MigrationAction: Up migration completed" ) );
        Assert.IsTrue( stdOut1.Contains( "Executed 2 migrations" ) );

        await migrationContainer.StartAsync();
        var (stdOut2, _) = await migrationContainer.GetLogsAsync();

        Assert.IsTrue( stdOut2.Contains( "Executed 0 migrations" ) );
    }


    [TestMethod]
    public async Task Should_Fail_WhenMigrationHasLock()
    {
        var migrationImage = await MongoDbMigrationContainer.BuildMigrationImageAsync();

        var migrationContainer1 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        var migrationContainer2 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        var migrationContainer3 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        var migrationContainer4 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );

        var migration1 = migrationContainer1.StartAsync();
        var migration2 = migrationContainer2.StartAsync();
        var migration3 = migrationContainer3.StartAsync();
        var migration4 = migrationContainer4.StartAsync();

        await Task.WhenAll( migration1, migration2, migration3, migration4 );

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
        Assert.IsTrue( allStdOut.Contains( "Executed 1 migrations" ) );
        Assert.IsTrue( allStdOut.Contains( "Executed 0 migrations" ) );
        Assert.IsTrue( allStdOut.Contains( "The migration lock is unavailable. Skipping migrations." ) );
    }
}
#endif
