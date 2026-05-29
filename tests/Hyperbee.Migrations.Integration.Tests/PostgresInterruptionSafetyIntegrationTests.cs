//#define INTEGRATIONS
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Postgres;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// ADR-0027 Tier 1 — Postgres ledger interruption-safety coverage.
//
// Proves the kind-domain widening (the Phase 0 blocker): the original
// CHECK (kind IN (0,1,2)) rejected the new InProgress=4 sentinel (and the
// pre-existing Recovery=3). InitializeAsync now widens to (0,1,2,3,4) via an
// idempotent DO-block, both for fresh tables and as an upgrade of legacy tables.
//
// Each test uses a unique schema in the shared container so the cases are
// isolated (fresh-create vs legacy-upgrade) without standing up extra
// containers. The store is internal, so it is resolved through the public
// AddPostgresMigrations DI registration (legacy IMigrationRecordStore alias).

[TestClass]
[DoNotParallelize]
[TestCategory( "LocalOnly" )]
public class PostgresInterruptionSafetyIntegrationTests
{
    private static Testcontainers.PostgreSql.PostgreSqlContainer _container;
    private static string _connectionString;

    [ClassInitialize( InheritanceBehavior.None )]
    public static async Task ClassSetup( TestContext context )
    {
        _container = new Testcontainers.PostgreSql.PostgreSqlBuilder( "postgres:16-alpine" )
            .WithDatabase( "isl" )
            .WithUsername( "isl" )
            .WithPassword( "isl" )
            .WithCleanUp( true )
            .Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    [ClassCleanup( InheritanceBehavior.None )]
    public static async Task ClassCleanup()
    {
        if ( _container != null )
            await _container.DisposeAsync();
    }

    private static IMigrationRecordStore BuildStore( string schema )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();
        services.AddSingleton( NpgsqlDataSource.Create( _connectionString ) );
        services.AddPostgresMigrations( opts => opts.SchemaName = schema );
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMigrationRecordStore>();
    }

    // Builds a real PostgresMigrationRunner + its store sharing one schema, so an
    // end-to-end interruption/restart can be exercised against a live database.
    private static (PostgresMigrationRunner Runner, IMigrationRecordStore Store, MigrationOptions Options) BuildRunner(
        string schema, bool forceResume, long toVersion )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();
        services.AddSingleton( NpgsqlDataSource.Create( _connectionString ) );
        services.AddPostgresMigrations( opts =>
        {
            opts.SchemaName = schema;
            opts.Profiles.Add( "isl-e2e" );   // only the e2e fixtures are in scope
            opts.ToVersion = toVersion;       // bound below the assembly's other fixtures
            opts.ForceResume = forceResume;
            opts.LockingEnabled = false;      // single-runner test; skip lock ceremony
        } );
        var provider = services.BuildServiceProvider();
        return (
            provider.GetRequiredService<PostgresMigrationRunner>(),
            provider.GetRequiredService<IMigrationRecordStore>(),
            provider.GetRequiredService<PostgresMigrationOptions>() );
    }

    [TestMethod]
    public async Task EndToEnd_Tier2_interrupt_rolls_back_clean_no_sentinel()
    {
        // Postgres is transactional (ITransactionalRecordStore), so an interrupted
        // migration is Tier-2 fail-CLEAN, not Tier-1 fail-closed: the runner rolls
        // back, leaving NO journal row and -- critically -- NO sentinel. The restart
        // simply re-runs; it does not throw MigrationInterruptedException.
        const string schema = "isl_e2e_tier2";

        var (runner1, store1, options) = BuildRunner( schema, forceResume: false, toVersion: 900 );
        await runner1.RunAsync(); // OCE thrown in body -> rollback; RunAsync swallows OCE

        var recordId = options.Conventions.GetRecordId( new E2E_Interrupting_Migration() );
        Assert.IsFalse( await store1.ExistsAsync( recordId ),
            "interrupted migration must not be journaled" );
        Assert.IsFalse( await store1.ExistsAsync( InProgressRecord.IdFor( recordId ) ),
            "Tier-2 leaves NO sentinel (transaction rolled back) -- distinct from Tier-1" );

        // Run 2: fresh runner restarts cleanly (no fail-closed lockout).
        var (runner2, _, _) = BuildRunner( schema, forceResume: false, toVersion: 900 );
        MigrationInterruptedException caught = null;
        try
        {
            await runner2.RunAsync();
        }
        catch ( MigrationInterruptedException ex )
        {
            caught = ex;
        }

        Assert.IsNull( caught, "Tier-2 restart must be clean -- no interruption lockout" );
    }

    [TestMethod]
    public async Task Tier2_body_and_journal_roll_back_and_commit_atomically()
    {
        // Direct proof that the migration BODY (a DML write enrolled in the ambient
        // transaction) and the JOURNAL write share one transaction: rollback undoes
        // BOTH; commit persists BOTH. Drives the scope directly (internal types
        // visible via InternalsVisibleTo per ADR-0028).
        const string schema = "isl_atomic";
        await using var dataSource = NpgsqlDataSource.Create( _connectionString );

        await using ( var ddl = dataSource.CreateCommand(
            $"CREATE SCHEMA IF NOT EXISTS {schema}; " +
            $"CREATE TABLE IF NOT EXISTS {schema}.sideeffect (id int PRIMARY KEY);" ) )
            await ddl.ExecuteNonQueryAsync();

        var store = BuildStore( schema );
        await store.InitializeAsync();
        var txStore = (ITransactionalRecordStore) store;

        async Task<long> SideEffectCount( int id )
        {
            await using var c = dataSource.CreateCommand( $"SELECT COUNT(*) FROM {schema}.sideeffect WHERE id = {id}" );
            return (long) await c.ExecuteScalarAsync();
        }

        // --- rollback case: body DML + journal both vanish ---
        var rollbackScope = await txStore.BeginTransactionAsync();
        var pgRollback = (PostgresMigrationTransaction) rollbackScope;
        using ( MigrationContext.Push( new MigrationContext { AmbientTransaction = rollbackScope } ) )
        {
            await using ( var ins = new NpgsqlCommand( $"INSERT INTO {schema}.sideeffect (id) VALUES (1)", pgRollback.Connection, pgRollback.Transaction ) )
                await ins.ExecuteNonQueryAsync();
            await store.WriteAsync( new MigrationRecord { Id = "9000.atomic_rollback" }, WritePrecondition.None );
        }
        await rollbackScope.RollbackAsync();
        await rollbackScope.DisposeAsync();

        Assert.IsFalse( await store.ExistsAsync( "9000.atomic_rollback" ), "journal row must roll back" );
        Assert.AreEqual( 0L, await SideEffectCount( 1 ), "body DML must roll back" );

        // --- commit case: body DML + journal both persist ---
        var commitScope = await txStore.BeginTransactionAsync();
        var pgCommit = (PostgresMigrationTransaction) commitScope;
        using ( MigrationContext.Push( new MigrationContext { AmbientTransaction = commitScope } ) )
        {
            await using ( var ins = new NpgsqlCommand( $"INSERT INTO {schema}.sideeffect (id) VALUES (2)", pgCommit.Connection, pgCommit.Transaction ) )
                await ins.ExecuteNonQueryAsync();
            await store.WriteAsync( new MigrationRecord { Id = "9001.atomic_commit" }, WritePrecondition.None );
        }
        await commitScope.CommitAsync();
        await commitScope.DisposeAsync();

        Assert.IsTrue( await store.ExistsAsync( "9001.atomic_commit" ), "journal row must commit" );
        Assert.AreEqual( 1L, await SideEffectCount( 2 ), "body DML must commit" );
    }

    [TestMethod]
    public async Task EndToEnd_ForceResume_reaps_and_completes()
    {
        const string schema = "isl_e2e_forceresume";

        // Seed: v900 already applied (so it is skipped, not re-thrown), and a
        // leftover sentinel for the succeeding v901 with no journal row.
        var (_, seedStore, options) = BuildRunner( schema, forceResume: false, toVersion: 901 );
        await seedStore.InitializeAsync();

        var interruptedId = options.Conventions.GetRecordId( new E2E_Interrupting_Migration() );
        await seedStore.WriteAsync(
            new MigrationRecord { Id = interruptedId, RunOn = DateTimeOffset.UtcNow },
            WritePrecondition.None );

        var succeedId = options.Conventions.GetRecordId( new E2E_Succeeding_Migration() );
        await seedStore.WriteAsync( InProgressRecord.Build( succeedId ), WritePrecondition.None );

        // Run with ForceResume: the v901 sentinel is reaped and the migration
        // re-runs to completion.
        var (runner, store, _) = BuildRunner( schema, forceResume: true, toVersion: 901 );
        await runner.RunAsync();

        Assert.IsTrue( await store.ExistsAsync( succeedId ), "v901 should have re-run and journaled" );
        Assert.IsFalse( await store.ExistsAsync( InProgressRecord.IdFor( succeedId ) ),
            "v901 sentinel should be reaped under ForceResume" );
    }

    [TestMethod]
    public async Task FreshInitialize_accepts_InProgress_sentinel_roundtrip()
    {
        var store = BuildStore( "isl_fresh" );
        await store.InitializeAsync();

        const string recordId = "100.sentinel_it";
        var sentinelId = InProgressRecord.IdFor( recordId );

        await store.WriteAsync( InProgressRecord.Build( recordId ), WritePrecondition.None );

        Assert.IsTrue( await store.ExistsAsync( sentinelId ), "sentinel row should be written" );
        var read = await store.ReadAsync( sentinelId );
        Assert.IsNotNull( read );
        Assert.AreEqual( MigrationRecordKind.InProgress, read.Kind );
    }

    [TestMethod]
    public async Task IntersectWithApplied_detects_sentinel_by_id_on_real_store()
    {
        // The restart pre-scan finds leftover sentinels via IntersectWithAppliedAsync
        // (kind-agnostic existence-by-id, ADR-0027). Prove the real Postgres
        // implementation honors that contract for a Kind=InProgress row.
        var store = BuildStore( "isl_intersect" );
        await store.InitializeAsync();

        const string recordId = "150.prescan_it";
        var sentinelId = InProgressRecord.IdFor( recordId );
        await store.WriteAsync( InProgressRecord.Build( recordId ), WritePrecondition.None );

        var found = await store.IntersectWithAppliedAsync( new[] { sentinelId, "nope.absent" } );

        Assert.IsTrue( found.Contains( sentinelId ), "pre-scan query must detect the sentinel id" );
        Assert.IsFalse( found.Contains( "nope.absent" ) );
    }

    [TestMethod]
    public async Task FreshInitialize_accepts_Recovery_kind()
    {
        // Recovery=3 was also rejected by the original (0,1,2) constraint.
        var store = BuildStore( "isl_recovery" );
        await store.InitializeAsync();

        var recovery = RecoveryRecord.Build( 2099L, "prod", new[] { 2001L } );
        await store.WriteAsync( recovery, WritePrecondition.None );

        var read = await store.ReadAsync( recovery.Id );
        Assert.IsNotNull( read );
        Assert.AreEqual( MigrationRecordKind.Recovery, read.Kind );
    }

    [TestMethod]
    public async Task LegacyConstraint_is_widened_on_Initialize()
    {
        // Simulate a pre-ADR-0027 deployment: table exists with the original
        // CHECK (kind IN (0,1,2)). InitializeAsync must drop+re-add the widened
        // constraint so a kind=4 sentinel write then succeeds.
        const string schema = "isl_legacy";
        await using var dataSource = NpgsqlDataSource.Create( _connectionString );

        var legacyDdl =
            $"CREATE SCHEMA IF NOT EXISTS {schema};" +
            $"CREATE TABLE IF NOT EXISTS {schema}.ledger (" +
            "  record_id character varying(255) PRIMARY KEY," +
            "  run_on timestamp without time zone NOT NULL," +
            "  checksum text NULL," +
            "  kind smallint NOT NULL DEFAULT 0," +
            "  replaces bigint[] NOT NULL DEFAULT ARRAY[]::bigint[]);" +
            $"ALTER TABLE {schema}.ledger ADD CONSTRAINT ledger_kind_check CHECK (kind IN (0, 1, 2));";

        await using ( var cmd = dataSource.CreateCommand( legacyDdl ) )
            await cmd.ExecuteNonQueryAsync();

        // sanity: a kind=4 insert is rejected by the legacy constraint
        await using ( var bad = dataSource.CreateCommand(
            $"INSERT INTO {schema}.ledger (record_id, run_on, kind) VALUES ('precheck', NOW(), 4)" ) )
        {
            var threw = false;
            try { await bad.ExecuteNonQueryAsync(); }
            catch ( PostgresException ) { threw = true; }
            Assert.IsTrue( threw, "legacy constraint should reject kind=4 before the upgrade" );
        }

        // run the store initialize -> widens the constraint
        var store = BuildStore( schema );
        await store.InitializeAsync();

        // now a kind=4 sentinel write succeeds through the store
        const string recordId = "200.upgrade_it";
        await store.WriteAsync( InProgressRecord.Build( recordId ), WritePrecondition.None );

        Assert.IsTrue( await store.ExistsAsync( InProgressRecord.IdFor( recordId ) ),
            "sentinel write should succeed after the constraint is widened" );
    }
}

// End-to-end fixtures, isolated under the "isl-e2e" profile and versioned below
// the assembly's other (OpenSearch) fixtures so ToVersion keeps them from running.
[Migration( 900, null, null, true, "isl-e2e" )]
[DataMigration]
public sealed class E2E_Interrupting_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => throw new OperationCanceledException( "simulated SIGTERM mid-migration" );
}

[Migration( 901, null, null, true, "isl-e2e" )]
[DataMigration]
public sealed class E2E_Succeeding_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}
#endif
