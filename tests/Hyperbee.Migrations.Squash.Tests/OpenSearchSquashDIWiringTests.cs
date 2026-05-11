using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.6: SquashStrategyDescriptor DI wiring (OpenSearch).
//
// Verifies AddOpenSearchMigrations registers the 6-component squash surface
// and consumers can resolve a SquashStrategyDescriptor that passes EnsureValid
// (per ADR-0019: ProviderId agreement across all 5 component instances).

[TestClass]
public class OpenSearchSquashDIWiringTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        // Minimal IConfiguration + OpenSearch client stub so the registration
        // factory works. Production consumers wire a real connection-string +
        // auth via AddOpenSearchClient; tests just need the DI graph to resolve.
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddSingleton( Substitute.For<IOpenSearchClient>() );

        services.AddOpenSearchMigrations();

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void AddOpenSearchMigrations_RegistersSnapshotCanonicalizer()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<OpenSearchSnapshotCanonicalizer>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddOpenSearchMigrations_RegistersDataOpClassifier()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<OpenSearchDataOpClassifier>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddOpenSearchMigrations_RegistersStrategy()
    {
        var provider = BuildProvider();
        var strategy = provider.GetRequiredService<RestStateDiffStrategy>();
        strategy.Should().NotBeNull();
        strategy.ProviderId.Should().Be( "opensearch" );
    }

    [TestMethod]
    public void AddOpenSearchMigrations_RegistersVerifier()
    {
        var provider = BuildProvider();
        var verifier = provider.GetRequiredService<OpenSearchSquashVerifier>();
        verifier.Should().NotBeNull();
        verifier.ProviderId.Should().Be( "opensearch" );
    }

    [TestMethod]
    public void AddOpenSearchMigrations_RegistersDescriptor_AndItPassesEnsureValid()
    {
        var provider = BuildProvider();

        // The descriptor's EnsureValid asserts ProviderId agreement; resolution
        // throws if any component returns a mismatched ProviderId.
        var descriptor = provider.GetRequiredService<SquashStrategyDescriptor>();

        descriptor.Should().NotBeNull();
        descriptor.TopologySignature.ProviderId.Should().Be( "opensearch" );
        descriptor.DataOpClassifier.Should().BeOfType<OpenSearchDataOpClassifier>();
        descriptor.Generator.Should().BeOfType<RestStateDiffStrategy>();
        descriptor.Verifier.Should().BeOfType<OpenSearchSquashVerifier>();
        descriptor.Canonicalizer.Should().BeOfType<OpenSearchSnapshotCanonicalizer>();
    }

    [TestMethod]
    public void AddOpenSearchMigrations_DescriptorResolvesAsSingleton()
    {
        var provider = BuildProvider();
        var d1 = provider.GetRequiredService<SquashStrategyDescriptor>();
        var d2 = provider.GetRequiredService<SquashStrategyDescriptor>();

        d1.Should().BeSameAs( d2, "SquashStrategyDescriptor is registered as a singleton; repeated resolution must return the same instance" );
    }

    [TestMethod]
    public void AddOpenSearchMigrations_DescriptorComponentsAreSingletons()
    {
        var provider = BuildProvider();
        var d = provider.GetRequiredService<SquashStrategyDescriptor>();

        d.Canonicalizer.Should().BeSameAs( provider.GetRequiredService<OpenSearchSnapshotCanonicalizer>() );
        d.DataOpClassifier.Should().BeSameAs( provider.GetRequiredService<OpenSearchDataOpClassifier>() );
        d.Generator.Should().BeSameAs( provider.GetRequiredService<RestStateDiffStrategy>() );
        d.Verifier.Should().BeSameAs( provider.GetRequiredService<OpenSearchSquashVerifier>() );
    }
}
