//#define INTEGRATIONS
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
        Assert.IsTrue( stdOut1.Contains( "Creating ledger scope `hyperbee`.`migrations`." ) );
        Assert.IsTrue( stdOut1.Contains( "Creating ledger collection `hyperbee`.`migrations`.`ledger`." ) );
        Assert.IsTrue( stdOut1.Contains( "Creating ledger primary index `hyperbee`.`migrations`.`ledger`." ) );


        // Check that migrations ran
        Assert.IsTrue( stdOut1.Contains( "CREATE BUCKET `migrationbucket`" ) );
        Assert.IsTrue( stdOut1.Contains( "CREATE PRIMARY INDEX idx_migrationbucket_primary ON `migrationbucket`" ) );
        Assert.IsTrue( stdOut1.Contains( "CREATE INDEX idx_migrationbucket_typeName ON `migrationbucket`" ) );
        Assert.IsTrue( stdOut1.Contains( "BUILD INDEX ON `migrationbucket`" ) );
        Assert.IsTrue( stdOut1.Contains( "UPSERT `0c81e0a030c64b8c80cbd05adf25e522/f90bcd5525b442dda8a5ee83e0987ec3` TO migrationbucket SCOPE _default COLLECTION _default" ) );
        Assert.IsTrue( stdOut1.Contains( "[1000] CreateInitialBuckets: Up migration completed" ) );
        Assert.IsTrue( stdOut1.Contains( "[2000] SecondaryAction: Up migration completed" ) );
        Assert.IsTrue( stdOut1.Contains( "[3000] MigrationAction: Up migration completed" ) );
        Assert.IsTrue( stdOut1.Contains( "Executed 3 migrations" ) );

        await migrationContainer.StartAsync();
        var (stdOut2, _) = await migrationContainer.GetLogsAsync();

        Assert.IsTrue( stdOut2.Contains( "Executed 0 migrations" ) );
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
        Assert.IsTrue( allStdOut.Contains( "Executed 3 migrations" ) );
        Assert.IsTrue( allStdOut.Contains( "The migration lock is unavailable. Skipping migrations." ) );
    }
}
#endif
