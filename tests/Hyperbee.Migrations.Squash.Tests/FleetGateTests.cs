using FluentAssertions;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 7 — Two-phase fleet readiness gate (per ADR-0019 A2 + A15).
//
// Generation-time half: SquashFleetGate.EnsureGenerable refuses when fleet
// members are mid-range -> MidRangeFleetException.
// Deploy-time half: SquashFleetGate.EnsureDeployable refuses when env isn't
// registered (UnregisteredEnvironmentException) or is stale beyond the
// staleness window (StaleFleetMemberException).

[TestClass]
public class FleetGateTests
{
    private static SquashMetadata MetadataFor(
        Dictionary<string, long> expectedFleetVersions,
        DateTimeOffset? generatedAt = null,
        TimeSpan? maxStalenessWindow = null )
    {
        return new SquashMetadata
        {
            ReplacesFromVersion = 1000,
            ReplacesToVersion = 1500,
            ProviderId = "postgres",
            Topology = new Dictionary<string, string> { ["server_major"] = "16" },
            CanonicalizerVersion = "postgres/1.0.0",
            ExpectedFleetVersions = expectedFleetVersions,
            MaxStalenessWindow = maxStalenessWindow ?? TimeSpan.FromDays( 30 ),
            CodegenToolVersion = "hyperbee-migrations/1.0.0",
            GeneratedAt = generatedAt ?? new DateTimeOffset( 2026, 5, 1, 0, 0, 0, TimeSpan.Zero )
        };
    }

    // -----------------------------------------------------------------
    // EnsureDeployable
    // -----------------------------------------------------------------

    [TestMethod]
    public void EnsureDeployable_EnvAtMinimum_Passes()
    {
        var metadata = MetadataFor( new Dictionary<string, long> { ["prod"] = 1500 } );

        var act = () => SquashFleetGate.EnsureDeployable(
            metadata, "prod",
            environmentLastAppliedVersion: 1500,
            now: new DateTimeOffset( 2026, 5, 6, 0, 0, 0, TimeSpan.Zero ) );

        act.Should().NotThrow();
    }

    [TestMethod]
    public void EnsureDeployable_EnvAboveMinimum_Passes()
    {
        var metadata = MetadataFor( new Dictionary<string, long> { ["prod"] = 1500 } );

        var act = () => SquashFleetGate.EnsureDeployable(
            metadata, "prod",
            environmentLastAppliedVersion: 1700, // already past
            now: new DateTimeOffset( 2026, 5, 6, 0, 0, 0, TimeSpan.Zero ) );

        act.Should().NotThrow();
    }

    [TestMethod]
    public void EnsureDeployable_EnvBelowMinimum_WithinStaleness_Passes()
    {
        var metadata = MetadataFor(
            new Dictionary<string, long> { ["prod"] = 1500 },
            generatedAt: new DateTimeOffset( 2026, 5, 1, 0, 0, 0, TimeSpan.Zero ),
            maxStalenessWindow: TimeSpan.FromDays( 30 ) );

        // 5 days after generation, env still below minimum: deploy allowed.
        var act = () => SquashFleetGate.EnsureDeployable(
            metadata, "prod",
            environmentLastAppliedVersion: 1300,
            now: new DateTimeOffset( 2026, 5, 6, 0, 0, 0, TimeSpan.Zero ) );

        act.Should().NotThrow( "env is below minimum but within the 30-day staleness window" );
    }

    [TestMethod]
    public void EnsureDeployable_EnvBelowMinimum_BeyondStaleness_Throws()
    {
        var metadata = MetadataFor(
            new Dictionary<string, long> { ["prod"] = 1500 },
            generatedAt: new DateTimeOffset( 2026, 4, 1, 0, 0, 0, TimeSpan.Zero ),
            maxStalenessWindow: TimeSpan.FromDays( 30 ) );

        // 35 days after generation, env still below minimum: refused.
        var act = () => SquashFleetGate.EnsureDeployable(
            metadata, "prod",
            environmentLastAppliedVersion: 1300,
            now: new DateTimeOffset( 2026, 5, 6, 0, 0, 0, TimeSpan.Zero ) );

        var ex = act.Should().Throw<StaleFleetMemberException>().Which;
        ex.EnvironmentName.Should().Be( "prod" );
        ex.ExpectedMinVersion.Should().Be( 1500 );
        ex.ActualVersion.Should().Be( 1300 );
        ex.MaxStalenessWindow.Should().Be( TimeSpan.FromDays( 30 ) );
        ex.Message.Should().Contain( "ADR-0019" );
    }

    [TestMethod]
    public void EnsureDeployable_UnregisteredEnv_Throws()
    {
        var metadata = MetadataFor( new Dictionary<string, long>
        {
            ["prod"] = 1500,
            ["staging"] = 1500
        } );

        var act = () => SquashFleetGate.EnsureDeployable(
            metadata, "qa", // not in manifest
            environmentLastAppliedVersion: 1700,
            now: new DateTimeOffset( 2026, 5, 6, 0, 0, 0, TimeSpan.Zero ) );

        var ex = act.Should().Throw<UnregisteredEnvironmentException>().Which;
        ex.EnvironmentName.Should().Be( "qa" );
        ex.RegisteredEnvironments.Should().BeEquivalentTo( new[] { "prod", "staging" } );
        ex.Message.Should().Contain( "qa" );
        ex.Message.Should().Contain( "prod" );
    }

    // -----------------------------------------------------------------
    // EnsureGenerable
    // -----------------------------------------------------------------

    [TestMethod]
    public void EnsureGenerable_AllMembersPastUpper_Passes()
    {
        var act = () => SquashFleetGate.EnsureGenerable(
            proposedReplacesFromVersion: 1000,
            proposedReplacesToVersion: 1500,
            fleetMembers: new[]
            {
                new FleetMemberState( "prod", 1500 ),
                new FleetMemberState( "staging", 1500 ),
                new FleetMemberState( "qa", 1700 )
            } );

        act.Should().NotThrow();
    }

    [TestMethod]
    public void EnsureGenerable_AllMembersBelowLower_Passes()
    {
        var act = () => SquashFleetGate.EnsureGenerable(
            proposedReplacesFromVersion: 1000,
            proposedReplacesToVersion: 1500,
            fleetMembers: new[]
            {
                new FleetMemberState( "fresh-env-1", 0 ),    // empty ledger
                new FleetMemberState( "fresh-env-2", 800 )   // below low bound
            } );

        act.Should().NotThrow( "members below the squash range will auto-mark or run-as-fresh on deploy" );
    }

    [TestMethod]
    public void EnsureGenerable_MidRangeMember_Throws()
    {
        var act = () => SquashFleetGate.EnsureGenerable(
            proposedReplacesFromVersion: 1000,
            proposedReplacesToVersion: 1500,
            fleetMembers: new[]
            {
                new FleetMemberState( "prod", 1500 ),
                new FleetMemberState( "staging", 1200 ), // mid-range — offender
                new FleetMemberState( "qa", 1500 )
            } );

        var ex = act.Should().Throw<MidRangeFleetException>().Which;
        ex.OffendingEnvironments.Should().HaveCount( 1 );
        ex.OffendingEnvironments[0].EnvironmentName.Should().Be( "staging" );
        ex.OffendingEnvironments[0].LastAppliedVersion.Should().Be( 1200 );
        ex.OffendingEnvironments[0].FirstMissingVersion.Should().Be( 1201 );
        ex.Message.Should().Contain( "staging" );
        ex.Message.Should().Contain( "ADR-0019" );
    }

    [TestMethod]
    public void EnsureGenerable_MultipleMidRange_AllReported()
    {
        var act = () => SquashFleetGate.EnsureGenerable(
            proposedReplacesFromVersion: 1000,
            proposedReplacesToVersion: 1500,
            fleetMembers: new[]
            {
                new FleetMemberState( "env-a", 1100 ),
                new FleetMemberState( "env-b", 1300 ),
                new FleetMemberState( "env-c", 1500 ) // ok
            } );

        var ex = act.Should().Throw<MidRangeFleetException>().Which;
        ex.OffendingEnvironments.Should().HaveCount( 2 );
        ex.OffendingEnvironments.Select( o => o.EnvironmentName ).Should().BeEquivalentTo( new[] { "env-a", "env-b" } );
    }

    [TestMethod]
    public void EnsureGenerable_RangeAtLowBound_NotMidRange()
    {
        // Edge case: env at exactly the low bound is mid-range BY DEFINITION
        // (it's applied >= low and < high). This is the right behavior — the
        // env has applied the first squashed version but not the rest.
        var act = () => SquashFleetGate.EnsureGenerable(
            proposedReplacesFromVersion: 1000,
            proposedReplacesToVersion: 1500,
            fleetMembers: new[] { new FleetMemberState( "env", 1000 ) } );

        act.Should().Throw<MidRangeFleetException>();
    }

    [TestMethod]
    public void EnsureGenerable_ReversedRange_Throws()
    {
        var act = () => SquashFleetGate.EnsureGenerable(
            proposedReplacesFromVersion: 1500,
            proposedReplacesToVersion: 1000,
            fleetMembers: Array.Empty<FleetMemberState>() );

        act.Should().Throw<ArgumentException>().WithMessage( "*to-version*" );
    }

    // -----------------------------------------------------------------
    // SquashOverrideEntry expiry
    // -----------------------------------------------------------------

    [TestMethod]
    public void SquashOverrideEntry_ExpiryCheck_RespectsCalendarTime()
    {
        var entry = new SquashOverrideEntry
        {
            EnvironmentName = "qa",
            TicketId = "FLEET-1234",
            Owner = "ops@example.com",
            Reason = "QA rebuild scheduled for 2026-Q3",
            Expires = new DateTimeOffset( 2026, 6, 1, 0, 0, 0, TimeSpan.Zero )
        };

        entry.IsExpired( new DateTimeOffset( 2026, 5, 31, 23, 59, 0, TimeSpan.Zero ) ).Should().BeFalse();
        entry.IsExpired( new DateTimeOffset( 2026, 6, 1, 0, 0, 0, TimeSpan.Zero ) ).Should().BeTrue();
        entry.IsExpired( new DateTimeOffset( 2026, 7, 1, 0, 0, 0, TimeSpan.Zero ) ).Should().BeTrue();
    }
}
