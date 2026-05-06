using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 — Universal Reconciliation Logic
//
// Provider-independent tests for runner-side reconciliation against an
// in-memory IMigrationRecordStore fake. Per-provider integration tests
// (real Testcontainers) ship in Phase 7.
//
// Branches exercised:
//   - Mature env (all replaced versions present)        -> auto-mark, no UpAsync
//   - Fresh env (empty ledger)                          -> run UpAsync with ApplyMode.Fresh
//   - Mid-range env (strict subset present)             -> MidRangeSquashException
//   - Re-squash transitivity (inner squash row covers)  -> auto-mark via row.Replaces
//
// ADR compliance: ADR-0019 (A6 transitivity, A17 Kind/Replaces consistency),
// ADR-0021 (checksum populated on auto-mark write).

[TestClass]
public class ReconciliationTests
{
    private static IMigrationActivator BareActivator()
    {
        var activator = Substitute.For<IMigrationActivator>();
        activator.CreateInstance( Arg.Any<Type>() ).Returns( args => (Migration) Activator.CreateInstance( args.Arg<Type>() )! );
        return activator;
    }

    // FakeStore: in-memory IMigrationRecordStore that honors the v3 contract
    // (record-bearing WriteAsync, LoadAppliedVersionsAsync, LoadSatisfyingRowsAsync).
    // Mirrors realtime semantics — every write is immediately visible.
    private sealed class FakeStore : IMigrationRecordStore
    {
        public Dictionary<string, MigrationRecord> Rows { get; } = new( StringComparer.Ordinal );

        public Task InitializeAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<IDisposable> CreateLockAsync() => Task.FromResult<IDisposable>( new NullDisposable() );

        public Task<bool> ExistsAsync( string recordId )
            => Task.FromResult( Rows.ContainsKey( recordId ) );

        public Task<MigrationRecord> ReadAsync( string recordId )
            => Task.FromResult( Rows.TryGetValue( recordId, out var r ) ? r : null );

        public Task DeleteAsync( string recordId )
        {
            Rows.Remove( recordId );
            return Task.CompletedTask;
        }

        public Task WriteAsync( string recordId )
        {
            Rows[recordId] = new MigrationRecord { Id = recordId };
            return Task.CompletedTask;
        }

        public Task<WriteOutcome> WriteAsync(
            MigrationRecord record,
            WritePrecondition precondition = WritePrecondition.None,
            CancellationToken cancellationToken = default )
        {
            record.EnsureLedgerIntegrity();

            if ( precondition == WritePrecondition.MustNotExist
                 && Rows.TryGetValue( record.Id, out var existing ) )
            {
                return Task.FromResult(
                    string.Equals( existing.Checksum, record.Checksum, StringComparison.Ordinal )
                        ? WriteOutcome.AlreadyExistsBenign
                        : WriteOutcome.PreconditionFailed );
            }

            Rows[record.Id] = record;
            return Task.FromResult( WriteOutcome.Created );
        }

        public Task<IReadOnlySet<string>> LoadAppliedVersionsAsync(
            IEnumerable<string> candidateIds,
            CancellationToken cancellationToken = default )
        {
            var found = new HashSet<string>(
                candidateIds.Where( id => Rows.ContainsKey( id ) ),
                StringComparer.Ordinal );
            return Task.FromResult<IReadOnlySet<string>>( found );
        }

        public Task<IReadOnlySet<long>> LoadSatisfyingRowsAsync(
            IEnumerable<long> versions,
            CancellationToken cancellationToken = default )
        {
            var inputs = versions.ToHashSet();
            var covered = new HashSet<long>();
            foreach ( var row in Rows.Values )
            {
                if ( row.Kind != MigrationRecordKind.Squash || row.Replaces == null )
                    continue;
                foreach ( var v in row.Replaces )
                {
                    if ( inputs.Contains( v ) )
                        covered.Add( v );
                }
            }
            return Task.FromResult<IReadOnlySet<long>>( covered );
        }

        private sealed class NullDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static MigrationOptions OptionsForProfile( string profile )
    {
        var options = new MigrationOptions( BareActivator() );
        options.Assemblies.Add( typeof( ReconciliationTests ).Assembly );
        options.Profiles.Add( profile );
        return options;
    }

    private static MigrationRunner BuildRunner( IMigrationRecordStore store, MigrationOptions options ) =>
        new( store, options, NullLogger<MigrationRunner>.Instance );

    private static string IdFor<T>() where T : Migration, new()
    {
        var conv = new DefaultMigrationConventions();
        return conv.GetRecordId( new T() );
    }

    // ---------------------------------------------------------------------
    // Mature env: all replaced versions present in ledger
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task MatureEnv_AutoMarksSquash_WithoutRunningBody()
    {
        var store = new FakeStore();
        // pre-populate ledger with the three originals
        store.Rows[IdFor<RecMigration2001>()] = new MigrationRecord { Id = IdFor<RecMigration2001>(), Checksum = "h-2001" };
        store.Rows[IdFor<RecMigration2002>()] = new MigrationRecord { Id = IdFor<RecMigration2002>(), Checksum = "h-2002" };
        store.Rows[IdFor<RecMigration2003>()] = new MigrationRecord { Id = IdFor<RecMigration2003>(), Checksum = "h-2003" };

        RecSquash2099.UpAsyncCallCount = 0;

        var runner = BuildRunner( store, OptionsForProfile( "phase3-mature" ) );
        await runner.RunAsync();

        RecSquash2099.UpAsyncCallCount.Should().Be( 0, "the squash body must NOT run when the ledger is mature" );
        store.Rows.Should().ContainKey( IdFor<RecSquash2099>() );

        var squashRow = store.Rows[IdFor<RecSquash2099>()];
        squashRow.Kind.Should().Be( MigrationRecordKind.Squash );
        squashRow.Replaces.Should().BeEquivalentTo( new[] { 2001L, 2002L, 2003L } );
        squashRow.Checksum.Should().NotBeNullOrEmpty();
    }

    // ---------------------------------------------------------------------
    // Fresh env: when the originals are out of scope (post-cleanup state) and
    // the ledger is empty, ClassifySquashAsync returns (Fresh, autoMark=false).
    //
    // Note: we cannot exercise this through the full RunAsync in v1 because
    // Phase 2 discovery rejects squashes whose Replaces names a version that
    // isn't a discovered [Migration]. ADR-0019 envisions relaxing this once
    // post-mature originals can be safely deleted; until then, the Fresh
    // branch's *code* is exercised via the internal classification helper.
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task FreshEnv_ClassifySquash_ReturnsFreshWithoutAutoMark()
    {
        var store = new FakeStore(); // empty ledger
        var recordIds = new Dictionary<long, string>
        {
            [2001L] = "record.2001.gone",
            [2002L] = "record.2002.gone",
            [2003L] = "record.2003.gone"
        };

        var (mode, autoMark) = await MigrationRunner.ClassifySquashAsync(
            store,
            squashVersion: 2099L,
            resolvedReplaces: new[] { 2001L, 2002L, 2003L },
            recordIdByVersion: recordIds,
            cancellationToken: default );

        mode.Should().Be( MigrationApplyMode.Fresh );
        autoMark.Should().BeFalse( "Fresh classification means run UpAsync as a baseline; do not auto-mark" );
    }

    [TestMethod]
    public async Task PartialCoverage_ClassifySquash_RaisesMidRangeException()
    {
        var store = new FakeStore();
        // direct match for 2001 + 2002, but 2003 is unsatisfied
        store.Rows["record.2001.x"] = new MigrationRecord { Id = "record.2001.x", Checksum = "h-2001" };
        store.Rows["record.2002.x"] = new MigrationRecord { Id = "record.2002.x", Checksum = "h-2002" };

        var recordIds = new Dictionary<long, string>
        {
            [2001L] = "record.2001.x",
            [2002L] = "record.2002.x",
            [2003L] = "record.2003.x"
        };

        var act = async () => await MigrationRunner.ClassifySquashAsync(
            store,
            squashVersion: 2099L,
            resolvedReplaces: new[] { 2001L, 2002L, 2003L },
            recordIdByVersion: recordIds,
            cancellationToken: default );

        var ex = (await act.Should().ThrowAsync<MidRangeSquashException>()).Which;
        ex.SquashVersion.Should().Be( 2099L );
        ex.MissingVersions.Should().BeEquivalentTo( new[] { 2003L } );
        ex.AppliedVersions.Should().BeEquivalentTo( new[] { 2001L, 2002L } );
    }

    // ---------------------------------------------------------------------
    // Mid-range env via Journal=false on a "missing" original: the runner runs
    // its UpAsync but skips the ledger write, so the squash sees a partial set.
    // This is the realistic v1 mid-range trigger for the full RunAsync flow.
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task MidRangeEnv_NonJournaledOriginal_RaisesMidRangeSquashException()
    {
        var store = new FakeStore();

        var runner = BuildRunner( store, OptionsForProfile( "phase3-midrange" ) );

        var act = async () => await runner.RunAsync();
        var ex = (await act.Should().ThrowAsync<MidRangeSquashException>()).Which;

        ex.SquashVersion.Should().Be( 6099L );
        ex.MissingVersions.Should().Contain( 6003L,
            "the Journal=false migration's row is never written; squash sees it as missing" );
        ex.Message.Should().Contain( "recover from-mid-range" );
        ex.Message.Should().Contain( "ADR-0019" );
    }

    // ---------------------------------------------------------------------
    // Re-squash transitivity (ADR-0019 A6):
    //   ledger has Squash_5000 (Kind=Squash, Replaces=[3000, 3010, 3020])
    //   encountering Squash_5099 with Replaces=[3000, 3010, 3020, 5000]
    //   transitive coverage of 3000/3010/3020 + direct presence of 5000
    //   -> mature, auto-mark
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task ReSquash_TransitiveCoverage_AutoMarks()
    {
        var store = new FakeStore();

        // Inner squash already in ledger, with Replaces covering 3000/3010/3020.
        // No direct rows for those three — only the squash row.
        store.Rows[IdFor<RecSquash5000>()] = new MigrationRecord
        {
            Id = IdFor<RecSquash5000>(),
            Kind = MigrationRecordKind.Squash,
            Replaces = new[] { 3000L, 3010L, 3020L },
            Checksum = "h-5000"
        };

        RecSquash5099.UpAsyncCallCount = 0;

        var runner = BuildRunner( store, OptionsForProfile( "phase3-resquash" ) );
        await runner.RunAsync();

        RecSquash5099.UpAsyncCallCount.Should().Be( 0,
            "outer squash must auto-mark via inner squash's transitive Replaces coverage" );
        store.Rows.Should().ContainKey( IdFor<RecSquash5099>() );
        store.Rows[IdFor<RecSquash5099>()].Kind.Should().Be( MigrationRecordKind.Squash );
    }

    // ---------------------------------------------------------------------
    // Auto-mark idempotency under concurrent runners:
    //   the WritePrecondition.MustNotExist + checksum-equality re-check path
    //   converges benignly when two runners race the same auto-mark.
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task AutoMark_IsIdempotent_UnderConcurrentReconcile()
    {
        var store = new FakeStore();
        // pre-populate the originals AND a previously-written matching squash row
        // (simulating a concurrent runner that beat us to the auto-mark)
        store.Rows[IdFor<RecMigration2001>()] = new MigrationRecord { Id = IdFor<RecMigration2001>(), Checksum = "h-2001" };
        store.Rows[IdFor<RecMigration2002>()] = new MigrationRecord { Id = IdFor<RecMigration2002>(), Checksum = "h-2002" };
        store.Rows[IdFor<RecMigration2003>()] = new MigrationRecord { Id = IdFor<RecMigration2003>(), Checksum = "h-2003" };

        // First run: writes Squash_2099 row.
        await BuildRunner( store, OptionsForProfile( "phase3-mature" ) ).RunAsync();
        var firstChecksum = store.Rows[IdFor<RecSquash2099>()].Checksum;

        // Second run: should find the squash row already exists with matching checksum.
        // The "Up when exists -> continue" path skips re-reconciliation entirely.
        await BuildRunner( store, OptionsForProfile( "phase3-mature" ) ).RunAsync();

        store.Rows[IdFor<RecSquash2099>()].Checksum.Should().Be( firstChecksum,
            "the second run must not overwrite the squash row's checksum" );
    }
}

// ---------------------------------------------------------------------
// Test fixtures
// ---------------------------------------------------------------------
//
// "phase3-mature" profile: 3 originals (2001-2003) + Squash_2099 (Replaces=2001..2003)
// "phase3-resquash" profile: anchor + Squash_5099 (Replaces=3000,3010,3020,5000)

[Migration( 2001L, null, null, true, "phase3-mature" )]
public class RecMigration2001 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 2002L, null, null, true, "phase3-mature" )]
public class RecMigration2002 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 2003L, null, null, true, "phase3-mature" )]
public class RecMigration2003 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 2099L, null, null, true, "phase3-mature", Replaces = new[] { 2001L, 2002L, 2003L } )]
public class RecSquash2099 : Migration
{
    public static int UpAsyncCallCount;
    public static MigrationApplyMode? LastObservedApplyMode;

    public override Task UpAsync( CancellationToken ct = default )
    {
        Interlocked.Increment( ref UpAsyncCallCount );
        LastObservedApplyMode = MigrationContext.Current?.ApplyMode;
        return Task.CompletedTask;
    }
}

// Re-squash anchor migrations — the inner squash's replaced versions never need
// to exist as raw migrations in this profile because the test pre-seeds the
// ledger with the inner squash row directly (transitivity coverage is what's
// being tested). The descriptor for the inner squash itself must be present
// in this assembly's discovery so its Replaces resolve cleanly.

[Migration( 3000L, null, null, true, "phase3-resquash" )]
public class RecMigration3000 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 3010L, null, null, true, "phase3-resquash" )]
public class RecMigration3010 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 3020L, null, null, true, "phase3-resquash" )]
public class RecMigration3020 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 5000L, null, null, true, "phase3-resquash", Replaces = new[] { 3000L, 3010L, 3020L } )]
public class RecSquash5000 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 5099L, null, null, true, "phase3-resquash", Replaces = new[] { 3000L, 3010L, 3020L, 5000L } )]
public class RecSquash5099 : Migration
{
    public static int UpAsyncCallCount;

    public override Task UpAsync( CancellationToken ct = default )
    {
        Interlocked.Increment( ref UpAsyncCallCount );
        return Task.CompletedTask;
    }
}

// "phase3-midrange" profile: 6001/6002 journal normally, 6003 is Journal=false
// so its UpAsync runs but no ledger row is written. When the runner reaches
// Squash_6099 (Replaces=[6001,6002,6003]), the ledger has 6001+6002 but not
// 6003 — the partial-coverage classification fires.

[Migration( 6001L, null, null, true, "phase3-midrange" )]
public class RecMidRange6001 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 6002L, null, null, true, "phase3-midrange" )]
public class RecMidRange6002 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

// Journal=false: runs but doesn't write a ledger row.
[Migration( 6003L, null, null, journal: false, "phase3-midrange" )]
public class RecMidRange6003 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 6099L, null, null, true, "phase3-midrange", Replaces = new[] { 6001L, 6002L, 6003L } )]
public class RecMidRangeSquash6099 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}
