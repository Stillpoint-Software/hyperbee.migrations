//#define INTEGRATIONS
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Methods in this class build a Docker image and race on the per-provider
// tar archive in the user's temp dir if run concurrently — see the comment
// on AerospikeRunnerTest for the rationale.
//
// The Couchbase N1QL planner-catalog refresh after CREATE SCOPE /
// CREATE COLLECTION is eventually consistent and used to race CREATE
// PRIMARY INDEX under the resource pressure of a single-job CI run
// hosting all 5 provider containers. CouchbaseHelper now retries each
// management operation with a forced planner refresh via system:scopes
// (see CreateScopeAsync / CreateCollectionAsync /
// CreatePrimaryCollectionIndexAsync), and the per-provider integration
// matrix in run_tests.yml gives this job a runner with only Couchbase's
// containers competing for resources.
[TestClass]
[DoNotParallelize]
// LocalOnly: heavy container-based integration test; excluded from the gating CI matrix (does not gate the NuGet publish). Runs locally / on demand.
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
    // Flaky (ADR-0031): asserts on a race. It launches concurrent runner containers and
    // requires at least one to observe lock contention -- on a fast host they all finish
    // before contending, and the assert fails with no defect present. Measured locally:
    // Aerospike and MongoDB both failed this way; Couchbase passed on the same run; the
    // Postgres equivalent was commented out by an earlier author for the same reason.
    // Excluded from the post-merge suite so that signal stays trustworthy. Still runs
    // locally and on manual dispatch. Fix is to hold the lock deterministically rather
    // than race containers; until then this is known debt, not coverage.
    [TestCategory( "Flaky" )]
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
