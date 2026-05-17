//#define INTEGRATIONS
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Integration.Tests.Container.Postgres;
using Hyperbee.Migrations.Providers.MongoDB;
using Hyperbee.Migrations.Providers.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Multi-runner Phase 2 Task 2.1 (R-P9): live two-provider integration.
//
// Spins both Postgres + MongoDB containers, registers both providers on a
// single IServiceCollection, resolves each typed runner, and runs each.
// The unit-tier MultiProviderHostTests cover DI shape; this suite proves
// the runners actually execute end-to-end against live clusters without
// interfering (independent ledgers, independent locks, independent
// discovery scopes).
//
// Per ADR-0023: base MigrationRunner resolution in a multi-provider host
// throws; operators MUST resolve the typed runner.

[TestClass]
[DoNotParallelize]
// LocalOnly: heavy container-based integration test; excluded from the gating CI matrix (does not gate the NuGet publish). Runs locally / on demand.
[TestCategory( "LocalOnly" )]
public class MultiProviderHostIntegrationTests
{
    [TestInitialize]
    public void Setup()
    {
        Assert.IsNotNull( PostgresTestContainer.Connection,
            "PostgresTestContainer must initialize before this test class runs." );
        Assert.IsNotNull( MongoDbTestContainer.Client,
            "MongoDbTestContainer must initialize before this test class runs." );
    }

    [TestMethod]
    public async Task TwoProviders_BothRunnersExecuteIndependently()
    {
        // Build a service collection that hosts BOTH providers. Configure
        // each with empty Profiles so reflection-based migration discovery
        // returns nothing (the test goal is the bootstrap + ledger paths
        // executing cleanly, not the actual application of migrations).
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        // AddLogging wires both ILoggerFactory + the open-generic ILogger<T>
        // services that PostgresRecordStore etc. require at construction.
        services.AddLogging();

        // Postgres dependency: NpgsqlDataSource from the live container.
        var pgConnString = ((Npgsql.NpgsqlConnection) PostgresTestContainer.Connection).ConnectionString;
        services.AddSingleton( new Npgsql.NpgsqlDataSourceBuilder( pgConnString ).Build() );

        // MongoDB dependency: client from the live container.
        services.AddSingleton( MongoDbTestContainer.Client );

        services.AddPostgresMigrations( opts =>
        {
            opts.SchemaName = "multi_pg";
            opts.Profiles = new[] { "two-runner-test-pg" };
            opts.Assemblies = new List<System.Reflection.Assembly> { typeof( MultiProviderHostIntegrationTests ).Assembly };
        } );
        services.AddMongoDBMigrations( opts =>
        {
            opts.DatabaseName = "multi_mongo";
            opts.Profiles = new[] { "two-runner-test-mongo" };
            opts.Assemblies = new List<System.Reflection.Assembly> { typeof( MultiProviderHostIntegrationTests ).Assembly };
        } );

        await using var sp = services.BuildServiceProvider();

        // Both typed runners must resolve.
        var pgRunner = sp.GetRequiredService<PostgresMigrationRunner>();
        var moRunner = sp.GetRequiredService<MongoDBMigrationRunner>();
        Assert.IsNotNull( pgRunner );
        Assert.IsNotNull( moRunner );
        Assert.AreNotSame( (object) pgRunner, (object) moRunner );

        // Base resolution MUST throw in the multi-provider host (ADR-0023 F1).
        var thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => sp.GetRequiredService<MigrationRunner>() );
        StringAssert.Contains( thrown.Message, "Postgres" );
        StringAssert.Contains( thrown.Message, "MongoDB" );

        // Both runners execute to completion against their own clusters.
        // The Postgres runner creates its schema + ledger + lock tables
        // against the Postgres container; the MongoDB runner creates its
        // ledger collection against the MongoDB container. Neither sees
        // the other's records.
        await pgRunner.RunAsync();
        await moRunner.RunAsync();

        // Defense-in-depth: re-running each is a no-op (both ledgers
        // already initialized; no migrations to apply).
        await pgRunner.RunAsync();
        await moRunner.RunAsync();
    }

    [TestMethod]
    public async Task TwoProviders_ParallelComposition_BothComplete()
    {
        // Per the multi-provider-hosts.md operator guide: when providers
        // are disjoint (no cross-store invariants), `Task.WhenAll` is the
        // valid parallel-composition pattern (provider locks are
        // independent per ADR-0005).
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        // AddLogging wires both ILoggerFactory + the open-generic ILogger<T>
        // services that PostgresRecordStore etc. require at construction.
        services.AddLogging();

        var pgConnString = ((Npgsql.NpgsqlConnection) PostgresTestContainer.Connection).ConnectionString;
        services.AddSingleton( new Npgsql.NpgsqlDataSourceBuilder( pgConnString ).Build() );
        services.AddSingleton( MongoDbTestContainer.Client );

        services.AddPostgresMigrations( opts =>
        {
            opts.SchemaName = "multi_pg_parallel";
            opts.Profiles = new[] { "parallel-test-pg" };
            opts.Assemblies = new List<System.Reflection.Assembly> { typeof( MultiProviderHostIntegrationTests ).Assembly };
        } );
        services.AddMongoDBMigrations( opts =>
        {
            opts.DatabaseName = "multi_mongo_parallel";
            opts.Profiles = new[] { "parallel-test-mongo" };
            opts.Assemblies = new List<System.Reflection.Assembly> { typeof( MultiProviderHostIntegrationTests ).Assembly };
        } );

        await using var sp = services.BuildServiceProvider();
        var pgRunner = sp.GetRequiredService<PostgresMigrationRunner>();
        var moRunner = sp.GetRequiredService<MongoDBMigrationRunner>();

        // Parallel execution -- max of both bootstrap costs, not sum.
        await Task.WhenAll( pgRunner.RunAsync(), moRunner.RunAsync() );
    }
}

#endif
