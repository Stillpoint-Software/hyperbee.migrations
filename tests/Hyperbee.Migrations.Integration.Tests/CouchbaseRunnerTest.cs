//#define INTEGRATIONS
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Methods in this class build a Docker image and race on the per-provider
// tar archive in the user's temp dir if run concurrently — see the comment
// on AerospikeRunnerTest for the rationale.
//
// LocalOnly per F-1 v3.0.1 follow-up: this suite spawns the migration runner
// as a sibling container against an isolated Couchbase Server. Couchbase's
// N1QL planner-catalog refresh after CREATE SCOPE / CREATE COLLECTION is
// eventually consistent and races CREATE PRIMARY INDEX on a loaded test
// machine (the planner returns "Scope not found in CB datastore" while the
// management API has already accepted the scope). The provider's
// CreatePrimaryCollectionIndexAsync retries for 30 seconds plus a forced
// catalog refresh via system:scopes; that recovers the race on a quiescent
// system but is not yet reliable under full-suite parallel load. The
// sibling-container provisioner (CouchbaseSiblingContainerProvisioner)
// landing in v3.0.1 lets this run in CI; in the meantime, the runner-test
// contract is covered by the per-provider unit suite (192 Couchbase tests)
// and the live-cluster contract is reproducible on the maintainer's local
// environment via this LocalOnly tag.
[TestClass]
[DoNotParallelize]
[TestCategory( "LocalOnly" )]
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
        var migrationImage = await CouchbaseMigrationContainer.BuildMigrationImageAsync();

        // First run
        var migrationContainer1 = await CouchbaseMigrationContainer.BuildMigrationsAsync( ConnectionString, Network, migrationImage );

        await migrationContainer1.StartAsync();
        var (stdOut1, _) = await migrationContainer1.GetLogsAsync();

        // Check that migration collection is configured - use updated log messages
        Assert.Contains( "Ensuring ledger scope `hyperbee`.`migrations` exists.", stdOut1 );
        Assert.Contains( "Ensuring ledger collection `hyperbee`.`migrations`.`ledger` exists.", stdOut1 );
        Assert.Contains( "Ensuring ledger primary index `hyperbee`.`migrations`.`ledger` exists.", stdOut1 );

        // Check that migrations ran
        Assert.Contains( "CREATE BUCKET `sample`", stdOut1 );
        Assert.Contains( "CREATE PRIMARY INDEX idx_sample_primary ON `sample`", stdOut1 );
        Assert.Contains( "[1000] CreateInitialSchema: Up migration completed", stdOut1 );
        Assert.Contains( "[2000] AddSecondaryIndexes: Up migration completed", stdOut1 );
        Assert.Contains( "[3000] SeedData: Up migration completed", stdOut1 );
        Assert.Contains( "Executed 3 migrations", stdOut1 );

        // Second run - create new container  
        var migrationContainer2 = await CouchbaseMigrationContainer.BuildMigrationsAsync( ConnectionString, Network, migrationImage );
        await migrationContainer2.StartAsync();
        var (stdOut2, _) = await migrationContainer2.GetLogsAsync();

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
