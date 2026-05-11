using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Driver;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.6: SquashStrategyDescriptor DI wiring (MongoDB).

[TestClass]
public class MongoDBSquashDIWiringTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddSingleton( Substitute.For<IMongoClient>() );
        services.AddMongoDBMigrations();
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void AddMongoDBMigrations_RegistersSnapshotCanonicalizer()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<MongoDBSnapshotCanonicalizer>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddMongoDBMigrations_RegistersDataOpClassifier()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<MongoDBDataOpClassifier>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddMongoDBMigrations_RegistersStrategy()
    {
        var provider = BuildProvider();
        var strategy = provider.GetRequiredService<IntrospectionSnapshotStrategy>();
        strategy.Should().NotBeNull();
        strategy.ProviderId.Should().Be( "mongodb" );
    }

    [TestMethod]
    public void AddMongoDBMigrations_RegistersVerifier()
    {
        var provider = BuildProvider();
        var verifier = provider.GetRequiredService<MongoDBSquashVerifier>();
        verifier.Should().NotBeNull();
        verifier.ProviderId.Should().Be( "mongodb" );
    }

    [TestMethod]
    public void AddMongoDBMigrations_RegistersDescriptor_AndItPassesEnsureValid()
    {
        var provider = BuildProvider();
        var descriptor = provider.GetRequiredService<SquashStrategyDescriptor>();

        descriptor.Should().NotBeNull();
        descriptor.TopologySignature.ProviderId.Should().Be( "mongodb" );
        descriptor.DataOpClassifier.Should().BeOfType<MongoDBDataOpClassifier>();
        descriptor.Generator.Should().BeOfType<IntrospectionSnapshotStrategy>();
        descriptor.Verifier.Should().BeOfType<MongoDBSquashVerifier>();
        descriptor.Canonicalizer.Should().BeOfType<MongoDBSnapshotCanonicalizer>();
    }

    [TestMethod]
    public void AddMongoDBMigrations_DescriptorResolvesAsSingleton()
    {
        var provider = BuildProvider();
        var d1 = provider.GetRequiredService<SquashStrategyDescriptor>();
        var d2 = provider.GetRequiredService<SquashStrategyDescriptor>();

        d1.Should().BeSameAs( d2 );
    }

    [TestMethod]
    public void AddMongoDBMigrations_DescriptorComponentsAreSingletons()
    {
        var provider = BuildProvider();
        var d = provider.GetRequiredService<SquashStrategyDescriptor>();

        d.Canonicalizer.Should().BeSameAs( provider.GetRequiredService<MongoDBSnapshotCanonicalizer>() );
        d.DataOpClassifier.Should().BeSameAs( provider.GetRequiredService<MongoDBDataOpClassifier>() );
        d.Generator.Should().BeSameAs( provider.GetRequiredService<IntrospectionSnapshotStrategy>() );
        d.Verifier.Should().BeSameAs( provider.GetRequiredService<MongoDBSquashVerifier>() );
    }
}
