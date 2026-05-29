using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hyperbee.Migrations.Tests;

// ADR-0027 back-compat: a custom IMigrationRecordStore that implements ONLY the
// legacy v2 surface (WriteAsync(string) + Exists/Read/Delete) and relies on the
// default-interface-method fallbacks for WriteAsync(record,...) and
// IntersectWithAppliedAsync must still get correct sentinel behavior:
//   - the sentinel write routes through the DIM -> WriteAsync(string)
//   - the pre-scan detects it through the DIM -> ExistsAsync loop
// This pins that a legacy store fails closed on an interrupted data migration.

[TestClass]
public class SentinelDimBackCompatTests
{
    // Legacy store: overrides only the non-DIM members. WriteAsync(record, ...),
    // IntersectWithAppliedAsync, and IntersectWithSquashedAsync use the interface
    // default implementations.
    private sealed class LegacyRecordStore : IMigrationRecordStore
    {
        private readonly HashSet<string> _ids = new( StringComparer.Ordinal );

        public IReadOnlyCollection<string> Ids => _ids;

        public Task InitializeAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<IDisposable> CreateLockAsync() => Task.FromResult<IDisposable>( new FakeLock() );

        public Task<bool> ExistsAsync( string recordId ) => Task.FromResult( _ids.Contains( recordId ) );
        public Task<MigrationRecord> ReadAsync( string recordId ) =>
            Task.FromResult( _ids.Contains( recordId ) ? new MigrationRecord { Id = recordId } : null );
        public Task DeleteAsync( string recordId )
        {
            _ids.Remove( recordId );
            return Task.CompletedTask;
        }
        public Task WriteAsync( string recordId )
        {
            _ids.Add( recordId );
            return Task.CompletedTask;
        }
    }

    private static MigrationOptions GetOptions( bool forceResume )
    {
        var activator = Substitute.For<IMigrationActivator>();
        activator.CreateInstance( Arg.Any<Type>() ).Returns( args => Activator.CreateInstance( args.Arg<Type>() ) );

        var options = new MigrationOptions( activator );
        options.Assemblies.Add( Assembly.GetExecutingAssembly() );
        options.Profiles.Add( "sentinel-test" );
        options.ToVersion = 102;
        options.ForceResume = forceResume;
        return options;
    }

    private static void SeedApplied( LegacyRecordStore store, MigrationOptions options, params Migration[] migrations )
    {
        foreach ( var m in migrations )
            store.WriteAsync( options.Conventions.GetRecordId( m ) ).GetAwaiter().GetResult();
    }

    // Seeds every standard (non-sentinel) fixture as applied so the runner skips
    // them — including the slow cron-delay fixtures (v7/v8) — leaving only the
    // sentinel-test fixtures in play. Mirrors RunnerTests.StandardAppliedStore.
    private static void SeedStandardAndSiblings( LegacyRecordStore store, MigrationOptions options )
    {
        SeedApplied( store, options,
            new First_Migration(), new Second_Migration(),
            new Cron_Delay_No_Stop_Migration(), new Cron_Delay_With_Stop_Migration(),
            new Stop_Migration(), new Cron_Migration(), new Interface_Continuous_Migration(),
            new Interrupting_Data_Migration(), new Structural_Only_Migration() );
    }

    [TestMethod]
    public async Task LegacyStore_via_DIM_fails_closed_on_interrupted_data_migration()
    {
        var options = GetOptions( forceResume: false );
        var store = new LegacyRecordStore();

        // standard + sibling fixtures applied so only v102 is a candidate
        SeedStandardAndSiblings( store, options );

        // leftover sentinel for v102 written through the legacy WriteAsync(string)
        var recordId = options.Conventions.GetRecordId( new Succeeding_Data_Migration() );
        await store.WriteAsync( InProgressRecord.IdFor( recordId ) );

        var logger = Substitute.For<ILogger<MigrationRunner>>();
        var runner = new MigrationRunner( store, options, logger );

        MigrationInterruptedException caught = null;
        try
        {
            await runner.RunAsync();
        }
        catch ( MigrationInterruptedException ex )
        {
            caught = ex;
        }

        Assert.IsNotNull( caught, "legacy DIM store must still fail closed" );
        Assert.AreEqual( recordId, caught.RecordId );
    }

    [TestMethod]
    public async Task LegacyStore_via_DIM_writes_and_reaps_sentinel_on_success()
    {
        // No leftover sentinel: a clean run of v102 must write the sentinel (via
        // DIM -> WriteAsync(string)) and reap it after journaling, leaving only the
        // real record. Proves the happy-path lifecycle works through the DIM.
        var options = GetOptions( forceResume: false );
        var store = new LegacyRecordStore();

        SeedStandardAndSiblings( store, options );

        var recordId = options.Conventions.GetRecordId( new Succeeding_Data_Migration() );

        var logger = Substitute.For<ILogger<MigrationRunner>>();
        var runner = new MigrationRunner( store, options, logger );

        await runner.RunAsync();

        Assert.IsTrue( store.Ids.Contains( recordId ), "v102 should be journaled" );
        Assert.IsFalse( store.Ids.Contains( InProgressRecord.IdFor( recordId ) ),
            "sentinel should be reaped after success" );
    }
}
