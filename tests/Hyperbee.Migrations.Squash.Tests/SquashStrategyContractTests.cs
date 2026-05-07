using FluentAssertions;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 5 — ISquashStrategy contract + NullSquashStrategy + composite descriptor.
//
// Provider-independent tests for the strategy contract surface. Real
// per-provider strategy implementations (Postgres v1) ship in Phase 6.
//
// ADR compliance: ADR-0019 A11 (composite descriptor; Unsupported deletion),
// A14 (topology schema versioning).

[TestClass]
public class SquashStrategyContractTests
{
    [TestMethod]
    public async Task NullSquashStrategy_ReturnsFailed_NamingRoadmapPhase()
    {
        var stub = new NullSquashStrategy( providerId: "mongodb", roadmapPhase: "v1.1" );

        var result = await stub.GenerateAsync(
            context: new TestContext( "mongodb", "Squash_3000", 3000L ),
            descriptors: Array.Empty<MigrationDescriptor>(),
            options: new SquashGenerationOptions() );

        var failed = result.Should().BeOfType<SquashGenerationResult.Failed>().Subject;
        failed.Detail.Should().Contain( "mongodb" );
        failed.Detail.Should().Contain( "v1.1" );
        failed.Detail.Should().Contain( "applying migrations individually" );
        failed.Cause.Should().BeNull();
    }

    [TestMethod]
    public void NullSquashStrategy_RejectsBlankProviderOrPhase()
    {
        var act1 = () => new NullSquashStrategy( providerId: "", roadmapPhase: "v1.1" );
        act1.Should().Throw<ArgumentException>();

        var act2 = () => new NullSquashStrategy( providerId: "mongodb", roadmapPhase: " " );
        act2.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Descriptor_EnsureValid_AcceptsConsistentComposite()
    {
        var sig = new TestTopology( providerId: "postgres" );
        var descriptor = new SquashStrategyDescriptor(
            TopologySignature: sig,
            DataOpClassifier: new TestClassifier(),
            Generator: new NullSquashStrategy( "postgres", "v1.0" ),
            Verifier: new TestVerifier( "postgres" ),
            Canonicalizer: new TestCanonicalizer( "postgres" ) );

        var act = () => descriptor.EnsureValid();
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Descriptor_EnsureValid_RejectsProviderIdMismatch()
    {
        var descriptor = new SquashStrategyDescriptor(
            TopologySignature: new TestTopology( "postgres" ),
            DataOpClassifier: new TestClassifier(),
            // generator is for a different provider — descriptor invalid
            Generator: new NullSquashStrategy( "mongodb", "v1.1" ),
            Verifier: new TestVerifier( "postgres" ),
            Canonicalizer: new TestCanonicalizer( "postgres" ) );

        var act = () => descriptor.EnsureValid();
        act.Should().Throw<MigrationException>().WithMessage( "*Generator provider id*mongodb*postgres*" );
    }

    [TestMethod]
    public void Descriptor_EnsureValid_RejectsNullComponents()
    {
        var descriptor = new SquashStrategyDescriptor(
            TopologySignature: null,
            DataOpClassifier: new TestClassifier(),
            Generator: new NullSquashStrategy( "postgres", "v1.0" ),
            Verifier: new TestVerifier( "postgres" ),
            Canonicalizer: new TestCanonicalizer( "postgres" ) );

        var act = () => descriptor.EnsureValid();
        act.Should().Throw<MigrationException>().WithMessage( "*TopologySignature is null*" );
    }

    [TestMethod]
    public void TopologySignature_IsCompatibleWith_SameProviderAndAxes_ReturnsTrue()
    {
        var a = new TestTopology( "postgres", ("server_major", "16"), ("collation", "icu") );
        var b = new TestTopology( "postgres", ("server_major", "16"), ("collation", "icu") );

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void TopologySignature_IsCompatibleWith_DifferentMajor_ReturnsFalse()
    {
        var a = new TestTopology( "postgres", ("server_major", "16") );
        var b = new TestTopology( "postgres", ("server_major", "17") );

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "server_major" );
    }

    [TestMethod]
    public void DataOpClassification_Defaults_AreSafelyConservative()
    {
        var c = new DataOpClassification(
            IsDataOp: false,
            RequiresPreservation: false,
            IsUnclassified: true,
            RequiresAnnotation: false );

        c.IsUnclassified.Should().BeTrue( "default-deny posture: unclassified requires operator action" );
        c.EmissionHint.Should().BeNull();
    }

    // ---------------- test helpers ----------------

    private sealed record TestContext( string ProviderId, string SquashName, long SquashVersion ) : ISquashGenerationContext;

    private sealed class TestTopology : ITopologySignature
    {
        public TestTopology( string providerId, params (string Key, string Value)[] axes )
        {
            ProviderId = providerId;
            Properties = axes.ToDictionary( a => a.Key, a => a.Value );
        }

        public int SchemaVersion => 1;
        public string ProviderId { get; }
        public IReadOnlyDictionary<string, string> Properties { get; }

        public bool IsCompatibleWith( ITopologySignature other, out string reason )
        {
            if ( !string.Equals( other?.ProviderId, ProviderId, StringComparison.Ordinal ) )
            {
                reason = "provider mismatch";
                return false;
            }

            foreach ( var kv in Properties )
            {
                if ( !other.Properties.TryGetValue( kv.Key, out var otherValue ) || otherValue != kv.Value )
                {
                    reason = $"axis '{kv.Key}' differs (this='{kv.Value}', other='{otherValue ?? "<missing>"}')";
                    return false;
                }
            }

            reason = null;
            return true;
        }
    }

    private sealed class TestClassifier : IDataOpClassifier
    {
        public DataOpClassification Classify( string statementOrCallSite ) =>
            new( IsDataOp: false, RequiresPreservation: false, IsUnclassified: true, RequiresAnnotation: false );
    }

    private sealed class TestVerifier : ISquashVerifier
    {
        public TestVerifier( string providerId ) { ProviderId = providerId; }
        public string ProviderId { get; }
        public Task<VerificationResult> VerifyAsync( ISquashGenerationContext context, SquashGenerationResult.Generated generated, CancellationToken cancellationToken = default ) =>
            Task.FromResult<VerificationResult>( new VerificationResult.Success( generated.Topology, TimeSpan.Zero ) );
    }

    private sealed class TestCanonicalizer : ISnapshotCanonicalizer
    {
        public TestCanonicalizer( string providerId ) { ProviderId = providerId; }
        public string ProviderId { get; }
        public string Canonicalize( string snapshot ) => snapshot;
        public string EmitScript( string canonicalContent ) => canonicalContent;
    }
}
