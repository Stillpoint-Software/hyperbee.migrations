using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hyperbee.Migrations.Tests;

// ADR-0028 Tier-2: when the record store is ITransactionalRecordStore and yields a
// scope, the runner wraps body + journal in that scope -- commit on success,
// rollback on failure -- and writes NO Tier-1 sentinel. These tests prove the
// runner's tier-selection + commit/rollback orchestration generically with a fake
// transactional store (the Postgres integration suite proves the real engine
// behavior). A store that does NOT implement ITransactionalRecordStore falls back
// to the Tier-1 sentinel path (covered by RunnerTests / SentinelDimBackCompatTests).

[TestClass]
public class RunnerTransactionTierTests
{
    private sealed class FakeScope : IMigrationTransactionScope
    {
        public int Commits;
        public int Rollbacks;
        public Task CommitAsync() { Commits++; return Task.CompletedTask; }
        public Task RollbackAsync() { Rollbacks++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransactionalStore : IMigrationRecordStore, ITransactionalRecordStore
    {
        private readonly HashSet<string> _ids = new( StringComparer.Ordinal );
        public FakeScope Scope { get; } = new();

        public IReadOnlyCollection<string> Ids => _ids;
        public bool WroteAnySentinel { get; private set; }

        public Task<IMigrationTransactionScope> BeginTransactionAsync( CancellationToken cancellationToken = default )
            => Task.FromResult<IMigrationTransactionScope>( Scope );

        public Task InitializeAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<IDisposable> CreateLockAsync() => Task.FromResult<IDisposable>( new FakeLock() );
        public Task<bool> ExistsAsync( string recordId ) => Task.FromResult( _ids.Contains( recordId ) );
        public Task<MigrationRecord> ReadAsync( string recordId ) =>
            Task.FromResult( _ids.Contains( recordId ) ? new MigrationRecord { Id = recordId } : null );
        public Task DeleteAsync( string recordId ) { _ids.Remove( recordId ); return Task.CompletedTask; }
        public Task WriteAsync( string recordId ) { Track( recordId ); return Task.CompletedTask; }

        public Task<WriteOutcome> WriteAsync( MigrationRecord record, WritePrecondition precondition = WritePrecondition.None, CancellationToken cancellationToken = default )
        {
            record.EnsureLedgerIntegrity();
            Track( record.Id );
            return Task.FromResult( WriteOutcome.Created );
        }

        public Task<IReadOnlySet<string>> IntersectWithAppliedAsync( IEnumerable<string> candidateIds, CancellationToken cancellationToken = default )
            => Task.FromResult<IReadOnlySet<string>>( candidateIds.Where( _ids.Contains ).ToHashSet( StringComparer.Ordinal ) );

        private void Track( string id )
        {
            if ( id.StartsWith( "inflight.", StringComparison.Ordinal ) )
                WroteAnySentinel = true;
            _ids.Add( id );
        }

        public void SeedApplied( MigrationOptions options, params Migration[] migrations )
        {
            foreach ( var m in migrations )
                _ids.Add( options.Conventions.GetRecordId( m ) );
        }
    }

    private static MigrationOptions Options()
    {
        var activator = Substitute.For<IMigrationActivator>();
        activator.CreateInstance( Arg.Any<Type>() ).Returns( a => Activator.CreateInstance( a.Arg<Type>() ) );
        var options = new MigrationOptions( activator );
        options.Assemblies.Add( Assembly.GetExecutingAssembly() );
        options.Profiles.Add( "sentinel-test" );
        options.ToVersion = 102;
        return options;
    }

    private static void SeedStandard( FakeTransactionalStore store, MigrationOptions options )
        => store.SeedApplied( options,
            new First_Migration(), new Second_Migration(),
            new Cron_Delay_No_Stop_Migration(), new Cron_Delay_With_Stop_Migration(),
            new Stop_Migration(), new Cron_Migration(), new Interface_Continuous_Migration(),
            new Interrupting_Data_Migration(), new Structural_Only_Migration() );

    [TestMethod]
    public async Task Tier2_success_commits_and_writes_no_sentinel()
    {
        var options = Options();
        var store = new FakeTransactionalStore();
        SeedStandard( store, options );   // only v102 (succeeding) runs

        var recordId = options.Conventions.GetRecordId( new Succeeding_Data_Migration() );
        var runner = new MigrationRunner( store, options, Substitute.For<ILogger<MigrationRunner>>() );

        await runner.RunAsync();

        Assert.AreEqual( 1, store.Scope.Commits, "scope must be committed on success" );
        Assert.AreEqual( 0, store.Scope.Rollbacks );
        Assert.IsTrue( store.Ids.Contains( recordId ), "journal row must be written" );
        Assert.IsFalse( store.WroteAnySentinel, "Tier-2 must not write a sentinel" );
    }

    [TestMethod]
    public async Task Tier2_interrupt_rolls_back_and_writes_no_sentinel()
    {
        var options = Options();
        options.ToVersion = 100;          // only v100 (throwing) runs
        var store = new FakeTransactionalStore();
        // Seed every standard fixture as applied EXCEPT the throwing v100, so it
        // is the only migration that runs (and the only scope action observed).
        store.SeedApplied( options,
            new First_Migration(), new Second_Migration(),
            new Cron_Delay_No_Stop_Migration(), new Cron_Delay_With_Stop_Migration(),
            new Stop_Migration(), new Cron_Migration(), new Interface_Continuous_Migration() );

        var runner = new MigrationRunner( store, options, Substitute.For<ILogger<MigrationRunner>>() );

        // RunAsync swallows OperationCanceledException after rolling back.
        await runner.RunAsync();

        Assert.AreEqual( 1, store.Scope.Rollbacks, "scope must be rolled back on interruption" );
        Assert.AreEqual( 0, store.Scope.Commits );
        Assert.IsFalse( store.WroteAnySentinel, "Tier-2 leaves no sentinel (transaction rolled back)" );
    }
}
