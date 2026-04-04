//#define INTEGRATIONS
using Aerospike.Client;
using Hyperbee.Migrations.Integration.Tests.Container.Aerospike;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
[TestClass]
public class AerospikeRunnerTest
{
    public IAsyncClient AsyncClient;
    public IAerospikeClient Client;
    public INetwork Network;

    [TestInitialize]
    public void Setup()
    {
        AsyncClient = AerospikeTestContainer.AsyncClient;
        Client = AerospikeTestContainer.Client;
        Network = AerospikeTestContainer.Network;
    }

    [TestMethod]
    public async Task Should_Succeed_WhenRunningUpTwice()
    {
        var migrationImage = await AerospikeMigrationContainer.BuildMigrationImageAsync();

        // First run
        var migrationContainer1 = await AerospikeMigrationContainer.BuildMigrationsAsync( Network, migrationImage );
        await migrationContainer1.StartAsync();

        var (stdOut1, _) = await migrationContainer1.GetLogsAsync();

        // Check that migrations ran
        Assert.Contains( "[1000] CreateInitialSets: Up migration started", stdOut1 );
        Assert.Contains( "[1000] CreateInitialSets: Up migration completed", stdOut1 );
        Assert.Contains( "[2000] AddSecondaryIndexes: Up migration started", stdOut1 );
        Assert.Contains( "[2000] AddSecondaryIndexes: Up migration completed", stdOut1 );
        Assert.Contains( "Executed 2 migrations", stdOut1 );

        // Verify resource migrations: index creation with WAIT
        Assert.Contains( "CREATE INDEX idx_email ON test.users (email) String", stdOut1 );
        Assert.Contains( "CREATE INDEX idx_active ON test.users (active) Numeric", stdOut1 );
        Assert.Contains( "CREATE INDEX idx_location ON test.users (location) Geo2DSphere", stdOut1 );
        Assert.Contains( "CREATE INDEX idx_role ON test.users (role) String", stdOut1 );
        Assert.Contains( "CREATE INDEX idx_category ON test.products (category) String", stdOut1 );
        Assert.Contains( "CREATE INDEX idx_price ON test.products (price) Numeric", stdOut1 );

        // Verify resource migrations: document seeding
        Assert.Contains( "UPSERT 'user-admin' TO test.users", stdOut1 );
        Assert.Contains( "UPSERT 'user-001' TO test.users", stdOut1 );
        Assert.Contains( "UPSERT 'user-002' TO test.users", stdOut1 );
        Assert.Contains( "UPSERT 'prod-001' TO test.products", stdOut1 );
        Assert.Contains( "UPSERT 'prod-002' TO test.products", stdOut1 );

        // Verify data was actually written to Aerospike
        var adminRecord = await AsyncClient.Get( null, CancellationToken.None, new Key( "test", "users", "user-admin" ) );
        Assert.IsNotNull( adminRecord, "Admin user record should exist in Aerospike" );
        Assert.AreEqual( "Admin User", adminRecord.GetString( "name" ) );
        Assert.AreEqual( "admin@example.com", adminRecord.GetString( "email" ) );

        var productRecord = await AsyncClient.Get( null, CancellationToken.None, new Key( "test", "products", "prod-001" ) );
        Assert.IsNotNull( productRecord, "Product record should exist in Aerospike" );
        Assert.AreEqual( "Widget", productRecord.GetString( "name" ) );

        // Verify indexes were created
        var node = Client.Nodes.FirstOrDefault();
        Assert.IsNotNull( node, "Cluster should have at least one node" );

        var indexInfo = Info.Request( node, "sindex/test" );
        Assert.Contains( "idx_email", indexInfo );
        Assert.Contains( "idx_active", indexInfo );
        Assert.Contains( "idx_category", indexInfo );

        // Second run - should be idempotent
        var migrationContainer2 = await AerospikeMigrationContainer.BuildMigrationsAsync( Network, migrationImage );
        await migrationContainer2.StartAsync();
        var (stdOut2, _) = await migrationContainer2.GetLogsAsync();

        Assert.Contains( "Executed 0 migrations", stdOut2 );
    }

    [TestMethod]
    public async Task Should_Fail_WhenMigrationHasLock()
    {
        var migrationImage = await AerospikeMigrationContainer.BuildMigrationImageAsync();

        var migrationContainer1 = await AerospikeMigrationContainer.BuildMigrationsAsync( Network, migrationImage );
        var migrationContainer2 = await AerospikeMigrationContainer.BuildMigrationsAsync( Network, migrationImage );
        var migrationContainer3 = await AerospikeMigrationContainer.BuildMigrationsAsync( Network, migrationImage );
        var migrationContainer4 = await AerospikeMigrationContainer.BuildMigrationsAsync( Network, migrationImage );

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

        var lockDetected =
            allStdOut.Contains( "The migration lock is unavailable. Skipping migrations.", StringComparison.Ordinal ) ||
            allStdOut.Contains( "Lock already exists", StringComparison.Ordinal ) ||
            allStdOut.Contains( "unable to create lock", StringComparison.Ordinal );

        Assert.IsTrue( lockDetected, "Expected lock failure/skip, but did not find it.\n" + allStdOut );
    }
}
#endif
