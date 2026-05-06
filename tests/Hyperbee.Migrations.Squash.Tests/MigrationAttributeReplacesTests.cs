using FluentAssertions;
using Hyperbee.Migrations.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 — MigrationAttribute + Discovery
//
// Tests for [Migration(version, Replaces=long[], ReplacesRange=string)]:
//   - Replaces array discovery
//   - ReplacesRange parsing ("1000-1500", "1000-1199, 1300, 1400-1450")
//   - Resolution against assembly's actual [Migration] versions in inclusive range
//   - Load-time validation: non-existent version, self-reference, duplicates
//   - MigrationApplyMode enum + MigrationContext.IsFreshInstall back-compat sugar
//
// See plan: docs/plans/active/migration-squashing-v1.md, Phase 2 (Tasks 2.1-2.4).
//
// ADR compliance: ADR-0019 (Replaces graph), ADR-0004 (reflection discovery).

[TestClass]
public class MigrationAttributeReplacesTests
{
    [TestMethod]
    public void ReplacesRangeParser_SinglePoint()
    {
        var set = ReplacesRangeParser.Parse( "1700" );
        set.Should().BeEquivalentTo( new[] { 1700L } );
    }

    [TestMethod]
    public void ReplacesRangeParser_InclusiveRange()
    {
        var set = ReplacesRangeParser.Parse( "1000-1003" );
        set.Should().BeEquivalentTo( new[] { 1000L, 1001L, 1002L, 1003L } );
    }

    [TestMethod]
    public void ReplacesRangeParser_MixedExpression()
    {
        var set = ReplacesRangeParser.Parse( "1000-1002, 1300, 1400-1401" );
        set.Should().BeEquivalentTo( new[] { 1000L, 1001L, 1002L, 1300L, 1400L, 1401L } );
    }

    [TestMethod]
    public void ReplacesRangeParser_EmptyAndNull_AreNoOps()
    {
        ReplacesRangeParser.Parse( null ).Should().BeEmpty();
        ReplacesRangeParser.Parse( "" ).Should().BeEmpty();
        ReplacesRangeParser.Parse( "   " ).Should().BeEmpty();
    }

    [TestMethod]
    public void ReplacesRangeParser_InvalidEndpoint_Throws()
    {
        var act = () => ReplacesRangeParser.Parse( "1000-abc" );
        act.Should().Throw<FormatException>().WithMessage( "*range*" );
    }

    [TestMethod]
    public void ReplacesRangeParser_ReversedRange_Throws()
    {
        var act = () => ReplacesRangeParser.Parse( "2000-1500" );
        act.Should().Throw<FormatException>().WithMessage( "*end*less than*start*" );
    }

    // ---------------------------------------------------------------------
    // Discovery validation: end-to-end via MigrationRunner.RunAsync
    // ---------------------------------------------------------------------

    private static IMigrationActivator BareActivator()
    {
        var activator = Substitute.For<IMigrationActivator>();
        activator.CreateInstance( Arg.Any<Type>() ).Returns( args => (Migration) Activator.CreateInstance( args.Arg<Type>() )! );
        return activator;
    }

    private static MigrationOptions OptionsForAssembly()
    {
        var options = new MigrationOptions( BareActivator() );
        options.Assemblies.Add( typeof( MigrationAttributeReplacesTests ).Assembly );
        options.Profiles.Add( "phase2-replaces" );
        return options;
    }

    [TestMethod]
    public void Discovery_NonExistentReplaces_RaisesMigrationLoadException()
    {
        var options = new MigrationOptions( BareActivator() );
        options.Assemblies.Add( typeof( MigrationAttributeReplacesTests ).Assembly );
        options.Profiles.Add( "phase2-bad-version" );

        var store = Substitute.For<IMigrationRecordStore>();
        store.InitializeAsync().Returns( Task.CompletedTask );
        store.CreateLockAsync().Returns( Task.FromResult<IDisposable>( new NullDisposable() ) );

        var runner = new MigrationRunner( store, options, NullLogger<MigrationRunner>.Instance );

        var act = () => runner.RunAsync().GetAwaiter().GetResult();
        act.Should().Throw<MigrationLoadException>()
            .WithMessage( "*9999*do not correspond*" );
    }

    [TestMethod]
    public void Discovery_SelfReference_RaisesMigrationLoadException()
    {
        var options = new MigrationOptions( BareActivator() );
        options.Assemblies.Add( typeof( MigrationAttributeReplacesTests ).Assembly );
        options.Profiles.Add( "phase2-self-ref" );

        var store = Substitute.For<IMigrationRecordStore>();
        store.InitializeAsync().Returns( Task.CompletedTask );
        store.CreateLockAsync().Returns( Task.FromResult<IDisposable>( new NullDisposable() ) );

        var runner = new MigrationRunner( store, options, NullLogger<MigrationRunner>.Instance );

        var act = () => runner.RunAsync().GetAwaiter().GetResult();
        act.Should().Throw<MigrationLoadException>()
            .WithMessage( "*self-references*" );
    }

    [TestMethod]
    public void Discovery_RangeStringResolves_AgainstAssemblyVersions()
    {
        // SquashGood declares ReplacesRange = "2001-2003" against an assembly
        // that has 2001/2002/2003 + 2099 (the squash itself). Resolution
        // succeeds; descriptor records the resolved sorted version set.
        var options = OptionsForAssembly();

        var store = Substitute.For<IMigrationRecordStore>();
        store.InitializeAsync().Returns( Task.CompletedTask );
        store.CreateLockAsync().Returns( Task.FromResult<IDisposable>( new NullDisposable() ) );
        store.ExistsAsync( Arg.Any<string>() ).Returns( Task.FromResult( false ) );
        store.WriteAsync(
            Arg.Any<MigrationRecord>(),
            Arg.Any<WritePrecondition>(),
            Arg.Any<CancellationToken>()
        ).Returns( Task.FromResult( WriteOutcome.Created ) );

        var runner = new MigrationRunner( store, options, NullLogger<MigrationRunner>.Instance );

        var act = () => runner.RunAsync().GetAwaiter().GetResult();
        act.Should().NotThrow();
    }

    // ---------------------------------------------------------------------
    // MigrationApplyMode + MigrationContext
    // ---------------------------------------------------------------------

    [TestMethod]
    public void MigrationContext_IsFreshInstall_MirrorsApplyMode()
    {
        new MigrationContext { ApplyMode = MigrationApplyMode.Fresh }
            .IsFreshInstall.Should().BeTrue();

        new MigrationContext { ApplyMode = MigrationApplyMode.PartialCatchUp }
            .IsFreshInstall.Should().BeFalse();
    }

    [TestMethod]
    public void MigrationContext_PushScope_RestoresPreviousOnDispose()
    {
        MigrationContext.Current.Should().BeNull();

        var outer = new MigrationContext { ApplyMode = MigrationApplyMode.Fresh };
        using ( MigrationContext.Push( outer ) )
        {
            MigrationContext.Current.Should().BeSameAs( outer );

            var inner = new MigrationContext { ApplyMode = MigrationApplyMode.PartialCatchUp };
            using ( MigrationContext.Push( inner ) )
                MigrationContext.Current.Should().BeSameAs( inner );

            MigrationContext.Current.Should().BeSameAs( outer );
        }

        MigrationContext.Current.Should().BeNull();
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

// ---------------------------------------------------------------------
// Test fixtures: synthetic migrations for discovery validation
// ---------------------------------------------------------------------

// The 5-arg constructor (version, startMethod, stopMethod, journal, ...profiles)
// is used unambiguously to bind profile names — the 2-arg form `[Migration(v, "name")]`
// resolves to the (long, string startMethod, ...) overload which would treat the
// profile string as a StartMethod and leave Profiles empty.

[Migration( 2001L, null, null, true, "phase2-replaces" )]
public class GoodMigration2001 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 2002L, null, null, true, "phase2-replaces" )]
public class GoodMigration2002 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 2003L, null, null, true, "phase2-replaces" )]
public class GoodMigration2003 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 2099L, null, null, true, "phase2-replaces", ReplacesRange = "2001-2003" )]
public class SquashGood : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 3000L, null, null, true, "phase2-bad-version", Replaces = new[] { 9999L } )]
public class SquashWithMissingReplaces : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 4001L, null, null, true, "phase2-self-ref" )]
public class SelfRefAnchor4001 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 4002L, null, null, true, "phase2-self-ref", Replaces = new[] { 4001L, 4002L } )]
public class SquashSelfReference : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}
