#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// R-21 #3 — IsmEndpointCapability semantics. The bootstrap step's network
// behavior is exercised by integration tests against a live cluster; here
// we pin the in-process invariants that don't need a cluster:
//
//   - Default state is unresolved (path is null).
//   - SetPrefix resolves the capability.
//   - Idempotent re-set with the same value is a no-op.
//   - Re-set with a different value throws (signals a bootstrap-logic bug).

[TestClass]
public class IsmEndpointCapabilityTests
{
    [TestMethod]
    public void Default_IsUnresolved()
    {
        var cap = new IsmEndpointCapability();
        cap.IsResolved.Should().BeFalse();
        cap.IsmPathPrefix.Should().BeNull();
    }

    [TestMethod]
    public void SetPrefix_Modern_Resolves()
    {
        var cap = new IsmEndpointCapability();
        cap.SetPrefix( IsmEndpointDetectStep.ModernPrefix );
        cap.IsResolved.Should().BeTrue();
        cap.IsmPathPrefix.Should().Be( "_plugins/_ism" );
    }

    [TestMethod]
    public void SetPrefix_Legacy_Resolves()
    {
        var cap = new IsmEndpointCapability();
        cap.SetPrefix( IsmEndpointDetectStep.LegacyPrefix );
        cap.IsmPathPrefix.Should().Be( "_opendistro/_ism" );
    }

    [TestMethod]
    public void SetPrefix_TwiceSameValue_NoOp()
    {
        var cap = new IsmEndpointCapability();
        cap.SetPrefix( IsmEndpointDetectStep.ModernPrefix );
        cap.SetPrefix( IsmEndpointDetectStep.ModernPrefix );  // idempotent
        cap.IsmPathPrefix.Should().Be( "_plugins/_ism" );
    }

    [TestMethod]
    public void SetPrefix_TwiceDifferentValues_Throws()
    {
        // The cluster's ISM surface is fixed for the lifetime of the
        // deployment. A divergent re-detection signals a bootstrap-logic
        // bug; throw so the bug surfaces immediately rather than masking
        // it with last-write-wins.
        var cap = new IsmEndpointCapability();
        cap.SetPrefix( IsmEndpointDetectStep.ModernPrefix );

        var act = () => cap.SetPrefix( IsmEndpointDetectStep.LegacyPrefix );
        act.Should().Throw<InvalidOperationException>()
            .Where( ex => ex.Message.Contains( "_plugins/_ism" )
                          && ex.Message.Contains( "_opendistro/_ism" ) );
    }

    [TestMethod]
    public void SetPrefix_NullOrWhitespace_Throws()
    {
        var cap = new IsmEndpointCapability();
        var act = () => cap.SetPrefix( "" );
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Constants_HoldExpectedPaths()
    {
        // Pin the path constants so the dispatcher's path construction
        // can't drift from the bootstrap step's probes.
        IsmEndpointDetectStep.ModernPrefix.Should().Be( "_plugins/_ism" );
        IsmEndpointDetectStep.LegacyPrefix.Should().Be( "_opendistro/_ism" );
    }
}
