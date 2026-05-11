using Aerospike.Client;
using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 Sev 1 C: SquashStrategyDescriptor DI wiring.
//
// Verifies that AddAerospikeMigrations registers the 6-component squash
// surface and that consumers can resolve a SquashStrategyDescriptor that
// passes EnsureValid (per ADR-0019: ProviderId agreement across all 5
// component instances).

[TestClass]
public class AerospikeSquashDIWiringTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        // Minimal IConfiguration + Aerospike client stub so the registration
        // factory works. Production consumers wire a real connection-string
        // path; tests just need the DI graph to resolve.
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddSingleton( Substitute.For<IAsyncClient>() );
        services.AddSingleton<IAerospikeClient>( provider => provider.GetRequiredService<IAsyncClient>() );

        services.AddAerospikeMigrations( opts => opts.Namespace = "test" );

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void AddAerospikeMigrations_RegistersSnapshotCanonicalizer()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<AerospikeSnapshotCanonicalizer>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddAerospikeMigrations_RegistersDataOpClassifier()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<AerospikeDataOpClassifier>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddAerospikeMigrations_RegistersStrategy()
    {
        var provider = BuildProvider();
        var strategy = provider.GetRequiredService<InfoSnapshotStrategy>();
        strategy.Should().NotBeNull();
        strategy.ProviderId.Should().Be( "aerospike" );
    }

    [TestMethod]
    public void AddAerospikeMigrations_RegistersVerifier()
    {
        var provider = BuildProvider();
        var verifier = provider.GetRequiredService<AerospikeSquashVerifier>();
        verifier.Should().NotBeNull();
        verifier.ProviderId.Should().Be( "aerospike" );
    }

    [TestMethod]
    public void AddAerospikeMigrations_RegistersDescriptor_AndItPassesEnsureValid()
    {
        var provider = BuildProvider();

        // The descriptor's constructor calls EnsureValid; if any component
        // returns a mismatched ProviderId, this resolution throws.
        var descriptor = provider.GetRequiredService<SquashStrategyDescriptor>();

        descriptor.Should().NotBeNull();
        descriptor.TopologySignature.ProviderId.Should().Be( "aerospike" );
        descriptor.DataOpClassifier.Should().BeOfType<AerospikeDataOpClassifier>();
        descriptor.Generator.Should().BeOfType<InfoSnapshotStrategy>();
        descriptor.Verifier.Should().BeOfType<AerospikeSquashVerifier>();
        descriptor.Canonicalizer.Should().BeOfType<AerospikeSnapshotCanonicalizer>();
    }

    [TestMethod]
    public void AddAerospikeMigrations_DescriptorResolvesAsSingleton()
    {
        var provider = BuildProvider();
        var d1 = provider.GetRequiredService<SquashStrategyDescriptor>();
        var d2 = provider.GetRequiredService<SquashStrategyDescriptor>();

        d1.Should().BeSameAs( d2, "SquashStrategyDescriptor is registered as a singleton; repeated resolution must return the same instance" );
    }

    [TestMethod]
    public void AddAerospikeMigrations_DescriptorComponentsAreSingletons()
    {
        var provider = BuildProvider();
        var d = provider.GetRequiredService<SquashStrategyDescriptor>();

        // Direct resolutions should return the SAME instances the descriptor
        // holds -- ensures no double-construction cost or state divergence.
        d.Canonicalizer.Should().BeSameAs( provider.GetRequiredService<AerospikeSnapshotCanonicalizer>() );
        d.DataOpClassifier.Should().BeSameAs( provider.GetRequiredService<AerospikeDataOpClassifier>() );
        d.Generator.Should().BeSameAs( provider.GetRequiredService<InfoSnapshotStrategy>() );
        d.Verifier.Should().BeSameAs( provider.GetRequiredService<AerospikeSquashVerifier>() );
    }
}
