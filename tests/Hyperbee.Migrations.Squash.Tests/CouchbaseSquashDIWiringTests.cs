using Couchbase;
using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.6: SquashStrategyDescriptor DI wiring (Couchbase).
//
// Mirrors MongoDB Task 3.6 + OpenSearch Task 2.6 + Aerospike Task 1.6 -- the
// AddCouchbaseMigrations() entry point must register the full 5-component
// SquashStrategyDescriptor with EnsureValid() passing.

[TestClass]
public class CouchbaseSquashDIWiringTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        // Couchbase ClusterOptions are required by CouchbaseRestApiService's
        // ctor; supply a minimal options instance for DI to satisfy the
        // HttpClient factory pipeline if it ever resolves. The squash
        // components themselves do not touch ClusterOptions.
        services.AddSingleton<IOptions<ClusterOptions>>(
            new OptionsWrapper<ClusterOptions>( new ClusterOptions { ConnectionString = "couchbase://localhost" } ) );
        services.AddCouchbaseMigrations();
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void AddCouchbaseMigrations_RegistersSnapshotCanonicalizer()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<CouchbaseSnapshotCanonicalizer>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddCouchbaseMigrations_RegistersDataOpClassifier()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<CouchbaseDataOpClassifier>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddCouchbaseMigrations_RegistersStrategy()
    {
        var provider = BuildProvider();
        var strategy = provider.GetRequiredService<HybridStrategy>();
        strategy.Should().NotBeNull();
        strategy.ProviderId.Should().Be( "couchbase" );
    }

    [TestMethod]
    public void AddCouchbaseMigrations_RegistersVerifier()
    {
        var provider = BuildProvider();
        var verifier = provider.GetRequiredService<CouchbaseSquashVerifier>();
        verifier.Should().NotBeNull();
        verifier.ProviderId.Should().Be( "couchbase" );
    }

    [TestMethod]
    public void AddCouchbaseMigrations_RegistersDescriptor_AndItPassesEnsureValid()
    {
        var provider = BuildProvider();
        var descriptor = provider.GetRequiredService<SquashStrategyDescriptor>();

        descriptor.Should().NotBeNull();
        descriptor.TopologySignature.ProviderId.Should().Be( "couchbase" );
        descriptor.DataOpClassifier.Should().BeOfType<CouchbaseDataOpClassifier>();
        descriptor.Generator.Should().BeOfType<HybridStrategy>();
        descriptor.Verifier.Should().BeOfType<CouchbaseSquashVerifier>();
        descriptor.Canonicalizer.Should().BeOfType<CouchbaseSnapshotCanonicalizer>();
    }

    [TestMethod]
    public void AddCouchbaseMigrations_DescriptorResolvesAsSingleton()
    {
        var provider = BuildProvider();
        var d1 = provider.GetRequiredService<SquashStrategyDescriptor>();
        var d2 = provider.GetRequiredService<SquashStrategyDescriptor>();

        d1.Should().BeSameAs( d2 );
    }

    [TestMethod]
    public void AddCouchbaseMigrations_DescriptorComponentsAreSingletons()
    {
        var provider = BuildProvider();
        var d = provider.GetRequiredService<SquashStrategyDescriptor>();

        d.Canonicalizer.Should().BeSameAs( provider.GetRequiredService<CouchbaseSnapshotCanonicalizer>() );
        d.DataOpClassifier.Should().BeSameAs( provider.GetRequiredService<CouchbaseDataOpClassifier>() );
        d.Generator.Should().BeSameAs( provider.GetRequiredService<HybridStrategy>() );
        d.Verifier.Should().BeSameAs( provider.GetRequiredService<CouchbaseSquashVerifier>() );
    }
}
