using FluentAssertions;
using Hyperbee.Migrations.Providers.Postgres;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 5 release-prep parity: SquashStrategyDescriptor DI wiring (Postgres).
//
// Mirrors AerospikeSquashDIWiringTests + MongoDBSquashDIWiringTests +
// OpenSearchSquashDIWiringTests + CouchbaseSquashDIWiringTests. Confirms
// AddPostgresMigrations registers all 5 squash components plus the composed
// SquashStrategyDescriptor whose ctor EnsureValid asserts ProviderId
// agreement across all 5 component instances.

[TestClass]
public class PostgresSquashDIWiringTests
{
    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );

        // NpgsqlDataSource has no default ctor, so build a real lazy one
        // (no network connection until first command). The squash
        // components themselves never touch the data source.
        services.AddSingleton(
            new Npgsql.NpgsqlDataSourceBuilder( "Host=localhost;Username=u;Password=p" ).Build() );

        services.AddPostgresMigrations();
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void AddPostgresMigrations_RegistersSnapshotCanonicalizer()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<PostgresSnapshotCanonicalizer>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddPostgresMigrations_RegistersDataOpClassifier()
    {
        var provider = BuildProvider();
        provider.GetRequiredService<PostgresDataOpClassifier>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddPostgresMigrations_RegistersStrategy()
    {
        var provider = BuildProvider();
        var strategy = provider.GetRequiredService<PgDumpSnapshotStrategy>();
        strategy.Should().NotBeNull();
        strategy.ProviderId.Should().Be( "postgres" );
    }

    [TestMethod]
    public void AddPostgresMigrations_RegistersVerifier()
    {
        var provider = BuildProvider();
        var verifier = provider.GetRequiredService<PostgresSquashVerifier>();
        verifier.Should().NotBeNull();
        verifier.ProviderId.Should().Be( "postgres" );
    }

    [TestMethod]
    public void AddPostgresMigrations_RegistersDescriptor_AndItPassesEnsureValid()
    {
        var provider = BuildProvider();
        var descriptor = provider.GetRequiredService<SquashStrategyDescriptor>();

        descriptor.Should().NotBeNull();
        descriptor.TopologySignature.ProviderId.Should().Be( "postgres" );
        descriptor.DataOpClassifier.Should().BeOfType<PostgresDataOpClassifier>();
        descriptor.Generator.Should().BeOfType<PgDumpSnapshotStrategy>();
        descriptor.Verifier.Should().BeOfType<PostgresSquashVerifier>();
        descriptor.Canonicalizer.Should().BeOfType<PostgresSnapshotCanonicalizer>();
    }

    [TestMethod]
    public void AddPostgresMigrations_DescriptorResolvesAsSingleton()
    {
        var provider = BuildProvider();
        var d1 = provider.GetRequiredService<SquashStrategyDescriptor>();
        var d2 = provider.GetRequiredService<SquashStrategyDescriptor>();
        d1.Should().BeSameAs( d2 );
    }

    [TestMethod]
    public void AddPostgresMigrations_DescriptorComponentsAreSingletons()
    {
        var provider = BuildProvider();
        var d = provider.GetRequiredService<SquashStrategyDescriptor>();

        d.Canonicalizer.Should().BeSameAs( provider.GetRequiredService<PostgresSnapshotCanonicalizer>() );
        d.DataOpClassifier.Should().BeSameAs( provider.GetRequiredService<PostgresDataOpClassifier>() );
        d.Generator.Should().BeSameAs( provider.GetRequiredService<PgDumpSnapshotStrategy>() );
        d.Verifier.Should().BeSameAs( provider.GetRequiredService<PostgresSquashVerifier>() );
    }
}
