//#define INTEGRATIONS
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;

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
        var migrationImage = await MongoDbMigrationContainer.BuildMigrationImageAsync();

        // First run
        var migrationContainer1 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        await migrationContainer1.StartAsync();

        var (stdOut1, _) = await migrationContainer1.GetLogsAsync();

        Assert.Contains( "[1000] Initial: Up migration started", stdOut1 );
        Assert.Contains( "[1000] Initial: Up migration completed", stdOut1 );
        Assert.Contains( "[2000] MigrationAction: Up migration started", stdOut1 );
        Assert.Contains( "[2000] MigrationAction: Up migration continuing", stdOut1 );
        Assert.Contains( "[2000] MigrationAction: Up migration completed", stdOut1 );
        Assert.Contains( "Executed 2 migrations", stdOut1 );

        // Second run - create new container
        var migrationContainer2 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        await migrationContainer2.StartAsync();
        var (stdOut2, _) = await migrationContainer2.GetLogsAsync();

        Assert.Contains( "Executed 0 migrations", stdOut2 );
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

        await Task.WhenAll( migration1, migration2, migration3 );

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
        // We expect a lock failure to occur somewhere in the concurrent runs.
        // Assert the "lock path" happened.
        var lockDetected =
          allStdOut.Contains( "The migration lock is unavailable. Skipping migrations.", StringComparison.Ordinal ) ||
          allStdOut.Contains( "CreateLockAsync Lock already exists", StringComparison.Ordinal ) ||
          allStdOut.Contains( "CreateLockAsync unable to create database lock", StringComparison.Ordinal );

        Assert.IsTrue( lockDetected, "Expected lock failure/skip, but did not find it.\n" + allStdOut );

        //duplicate-key OR lock-exists
        var lockCauseDetected =
            allStdOut.Contains( "DuplicateKey", StringComparison.Ordinal ) ||
            allStdOut.Contains( "E11000 duplicate key error", StringComparison.Ordinal ) ||
            allStdOut.Contains( "Lock already exists", StringComparison.Ordinal );

        Assert.IsTrue( lockCauseDetected, "Expected lock cause evidence (duplicate key or lock exists), but did not find it.\n" + allStdOut );
    }
}
#endif
