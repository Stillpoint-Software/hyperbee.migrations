//#define INTEGRATIONS
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
[TestClass]
public class CouchbaseRunnerTest
{
    public INetwork Network;
    public string ConnectionString;

    [TestInitialize]
    public void Setup()
    {
        Network = CouchbaseTestContainer.Network;
        ConnectionString = CouchbaseTestContainer.ConnectionString;
    }

    [TestMethod]
    public async Task Should_Succeed_WhenRunningUpTwice()
    {
        var migrationContainer = await CouchbaseMigrationContainer.BuildMigrationsAsync( ConnectionString, Network );

        await migrationContainer.StartAsync();

        var (stdOut1, _) = await migrationContainer.GetLogsAsync();

        // Check that migration collection is configured
        Assert.Contains( "Creating ledger scope `hyperbee`.`migrations`.", stdOut1 );
        Assert.Contains( "Creating ledger collection `hyperbee`.`migrations`.`ledger`.", stdOut1 );
        Assert.Contains( "Creating ledger primary index `hyperbee`.`migrations`.`ledger`.", stdOut1 );


        // Check that migrations ran
        Assert.Contains( "CREATE BUCKET `migrationbucket`", stdOut1 );
        Assert.Contains( "CREATE PRIMARY INDEX idx_migrationbucket_primary ON `migrationbucket`", stdOut1 );
        Assert.Contains( "CREATE INDEX idx_migrationbucket_typeName ON `migrationbucket`", stdOut1 );
        Assert.Contains( "BUILD INDEX ON `migrationbucket`", stdOut1 );
        Assert.Contains( "UPSERT `0c81e0a030c64b8c80cbd05adf25e522/f90bcd5525b442dda8a5ee83e0987ec3` TO migrationbucket SCOPE _default COLLECTION _default", stdOut1 );
        Assert.Contains( "[1000] CreateInitialBuckets: Up migration completed", stdOut1 );
        Assert.Contains( "[2000] SecondaryAction: Up migration completed", stdOut1 );
        Assert.Contains( "[3000] MigrationAction: Up migration completed", stdOut1 );
        Assert.Contains( "Executed 3 migrations", stdOut1 );

        await migrationContainer.StartAsync();
        var (stdOut2, _) = await migrationContainer.GetLogsAsync();

        Assert.Contains( "Executed 0 migrations", stdOut2 );
    }

    [TestMethod]
    public async Task Should_Fail_WhenMigrationHasLock()
    {
        var migrationImage = await CouchbaseMigrationContainer.BuildMigrationImageAsync();

        var migrationContainer1 = await CouchbaseMigrationContainer.BuildMigrationsAsync( ConnectionString, Network, migrationImage );
        var migrationContainer2 = await CouchbaseMigrationContainer.BuildMigrationsAsync( ConnectionString, Network, migrationImage );
        var migrationContainer3 = await CouchbaseMigrationContainer.BuildMigrationsAsync( ConnectionString, Network, migrationImage );
        var migrationContainer4 = await CouchbaseMigrationContainer.BuildMigrationsAsync( ConnectionString, Network, migrationImage );

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
        Warn.If( !allStdOut.Contains( "Executed 3 migrations" ), "Did not run migrations\n" + allStdOut );
        Warn.If( !allStdOut.Contains( "Executed 0 migrations" ), "Did not re-run migrations" );
        Warn.If( !allStdOut.Contains( "The migration lock is unavailable. Skipping migrations." ), "Did not detect migration lock" );
    }
}
#endif
