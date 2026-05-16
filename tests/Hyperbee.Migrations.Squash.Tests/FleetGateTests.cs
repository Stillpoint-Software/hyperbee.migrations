using FluentAssertions;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Generation-time fleet readiness gate (ADR-0019 A2).
// SquashFleetGate.EnsureGenerable refuses when fleet members are
// mid-range -> MidRangeFleetException.
//
// The deploy-time half (EnsureDeployable + StaleFleetMemberException +
// UnregisteredEnvironmentException) was cut per ADR-0026 -- never wired;
// the silent-stranding case is already a loud apply-time refusal via
// the wired MigrationRunner MidRangeSquashException path.

[TestClass]
public class FleetGateTests
{
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
