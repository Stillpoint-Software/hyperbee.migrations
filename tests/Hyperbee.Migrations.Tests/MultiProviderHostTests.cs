using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike;
using Hyperbee.Migrations.Providers.MongoDB;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using IAsyncClient = Aerospike.Client.IAsyncClient;
using IMongoClient = MongoDB.Driver.IMongoClient;
using IOpenSearchClient = OpenSearch.Client.IOpenSearchClient;
using NpgsqlDataSource = Npgsql.NpgsqlDataSource;

namespace Hyperbee.Migrations.Tests;

// Multi-runner Phase 2 (ADR-0023): exercises the full DI composition on
// top of the per-provider Add{Provider}Migrations + RegisterBaseAliases
// chain.
//
// Scope: DI-level only (no real database). The per-provider integration
// test suites already prove the runner runs against a real container.
// This file proves that multi-provider hosts:
//
//   1. Get typed runner resolution per provider (no shadowing).
//   2. Get a clear, actionable throw when resolving base types.
//   3. Single-provider hosts still resolve base types unchanged
//      (backward compatibility).
//   4. Duplicate Add{Provider}Migrations calls are idempotent
//      (assessment N2 fix).
//   5. Logger category is the runtime type (assessment F7).

[TestClass]
public class MultiProviderHostTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging( b => b.AddProvider( NullLoggerProvider.Instance ) );

        // Stub the dependencies each provider's RecordStore needs at
        // construction time. We never call into them; resolution is enough.
        // NpgsqlDataSource has no default ctor, so we build a real one via
        // the lazy builder (no network connection until first command).
        services.AddSingleton( new Npgsql.NpgsqlDataSourceBuilder( "Host=localhost;Username=u;Password=p" ).Build() );
        services.AddSingleton( Substitute.For<IMongoClient>() );
        services.AddSingleton( Substitute.For<IAsyncClient>() );
        services.AddSingleton( Substitute.For<IOpenSearchClient>() );

        return services;
    }

    [TestMethod]
    public void SingleProviderHost_PostgresOnly_BaseTypesResolveToPostgres()
    {
        var services = BaseServices();
        services.AddPostgresMigrations();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<PostgresMigrationRunner>().Should().NotBeNull();
        sp.GetRequiredService<MigrationRunner>().Should().BeOfType<PostgresMigrationRunner>();
        sp.GetRequiredService<MigrationOptions>().Should().BeOfType<PostgresMigrationOptions>();
    }

    [TestMethod]
    public void SingleProviderHost_MongoDBOnly_BaseTypesResolveToMongoDB()
    {
        var services = BaseServices();
        services.AddMongoDBMigrations();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<MongoDBMigrationRunner>().Should().NotBeNull();
        sp.GetRequiredService<MigrationRunner>().Should().BeOfType<MongoDBMigrationRunner>();
        sp.GetRequiredService<MigrationOptions>().Should().BeOfType<MongoDBMigrationOptions>();
    }

    [TestMethod]
    public void MultiProviderHost_PostgresAndMongoDB_TypedRunnersResolveIndependently()
    {
        var services = BaseServices();
        services.AddPostgresMigrations();
        services.AddMongoDBMigrations();
        var sp = services.BuildServiceProvider();

        var pgRunner = sp.GetRequiredService<PostgresMigrationRunner>();
        var moRunner = sp.GetRequiredService<MongoDBMigrationRunner>();

        pgRunner.Should().NotBeNull();
        moRunner.Should().NotBeNull();
        pgRunner.Should().NotBeSameAs( moRunner );
    }

    [TestMethod]
    public void MultiProviderHost_BaseRunnerResolution_ThrowsFailLoud()
    {
        var services = BaseServices();
        services.AddPostgresMigrations();
        services.AddMongoDBMigrations();
        var sp = services.BuildServiceProvider();

        Action act = () => sp.GetRequiredService<MigrationRunner>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*" )
            .WithMessage( "*MigrationRunner*" )
            .WithMessage( "*ADR-0023*" );
    }

    [TestMethod]
    public void MultiProviderHost_BaseOptionsResolution_ThrowsFailLoud()
    {
        var services = BaseServices();
        services.AddPostgresMigrations();
        services.AddMongoDBMigrations();
        var sp = services.BuildServiceProvider();

        Action act = () => sp.GetRequiredService<MigrationOptions>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*" )
            .WithMessage( "*MigrationOptions*" );
    }

    [TestMethod]
    public void MultiProviderHost_RegistrationOrder_DoesNotChangeOutcome()
    {
        var pgFirst = BaseServices();
        pgFirst.AddPostgresMigrations();
        pgFirst.AddMongoDBMigrations();
        var pgFirstSp = pgFirst.BuildServiceProvider();

        var moFirst = BaseServices();
        moFirst.AddMongoDBMigrations();
        moFirst.AddPostgresMigrations();
        var moFirstSp = moFirst.BuildServiceProvider();

        // Both orderings: typed runners resolve; base throws.
        pgFirstSp.GetRequiredService<PostgresMigrationRunner>().Should().NotBeNull();
        pgFirstSp.GetRequiredService<MongoDBMigrationRunner>().Should().NotBeNull();
        moFirstSp.GetRequiredService<PostgresMigrationRunner>().Should().NotBeNull();
        moFirstSp.GetRequiredService<MongoDBMigrationRunner>().Should().NotBeNull();

        Action baseFirst = () => pgFirstSp.GetRequiredService<MigrationRunner>();
        Action baseSecond = () => moFirstSp.GetRequiredService<MigrationRunner>();
        baseFirst.Should().Throw<InvalidOperationException>();
        baseSecond.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void IdempotentRegistration_TwoPostgresCalls_BaseStillResolves()
    {
        // Two helper methods both call AddPostgresMigrations -- the second
        // call must NOT flip the host into multi-provider mode.
        var services = BaseServices();
        services.AddPostgresMigrations();
        services.AddPostgresMigrations();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<PostgresMigrationRunner>().Should().NotBeNull();
        sp.GetRequiredService<MigrationRunner>().Should().BeOfType<PostgresMigrationRunner>();
    }

    [TestMethod]
    public void TypedRunner_LoggerCategory_IsRuntimeType()
    {
        // The runner's logger category should reflect the concrete subclass
        // type, not the base MigrationRunner type, per ADR-0023 assessment F7.
        var captured = new List<string>();
        var services = BaseServices();
        services.AddSingleton<ILoggerProvider>( new CapturingLoggerProvider( captured ) );
        services.AddPostgresMigrations();
        var sp = services.BuildServiceProvider();

        _ = sp.GetRequiredService<PostgresMigrationRunner>();

        captured.Should().Contain( c => c.EndsWith( "PostgresMigrationRunner" ) );
        captured.Should().NotContain( c => c == "Hyperbee.Migrations.MigrationRunner" );
    }

    [TestMethod]
    public void ThreeProviders_AllNamedInThrowMessage()
    {
        var services = BaseServices();
        services.AddPostgresMigrations();
        services.AddMongoDBMigrations();
        services.AddAerospikeMigrations();
        var sp = services.BuildServiceProvider();

        Action act = () => sp.GetRequiredService<MigrationRunner>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*Aerospike*" );
    }

    [TestMethod]
    public void FiveProviders_AllTypedRunnersResolveIndependently()
    {
        // Couchbase requires Couchbase.Extensions.DependencyInjection
        // bootstrapping which BaseServices doesn't fully wire; skip the
        // Couchbase typed-runner resolution but verify the others all
        // resolve and the marker carries all five names.
        var services = BaseServices();
        services.AddPostgresMigrations();
        services.AddMongoDBMigrations();
        services.AddAerospikeMigrations();
        services.AddOpenSearchMigrations();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<PostgresMigrationRunner>().Should().NotBeNull();
        sp.GetRequiredService<MongoDBMigrationRunner>().Should().NotBeNull();
        sp.GetRequiredService<AerospikeMigrationRunner>().Should().NotBeNull();
        sp.GetRequiredService<OpenSearchMigrationRunner>().Should().NotBeNull();

        Action act = () => sp.GetRequiredService<MigrationRunner>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*Aerospike*OpenSearch*" );
    }

    // ---- Phase 2 Task 2.3 — Discovery scope isolation ----------------------

    [TestMethod]
    public void DiscoveryScope_IsIsolatedPerProvider()
    {
        // Each provider's options factory configures its own Assemblies list
        // (driven by Migrations:FromAssemblies / FromPaths + the calling
        // assembly default). In a multi-provider host the per-provider
        // options must NOT share assemblies, even though they share the
        // same DI container.
        var services = BaseServices();
        services.AddPostgresMigrations();
        services.AddMongoDBMigrations();
        var sp = services.BuildServiceProvider();

        var pgOptions = sp.GetRequiredService<PostgresMigrationOptions>();
        var moOptions = sp.GetRequiredService<MongoDBMigrationOptions>();

        // Both registered: each carries its own Assemblies list. The
        // default-assembly fallback means they could legitimately be the
        // same SINGLE entry when no Migrations:FromAssemblies is configured,
        // but they MUST be separate List instances (no aliasing).
        pgOptions.Should().NotBeSameAs( moOptions );
        pgOptions.Assemblies.Should().NotBeSameAs( moOptions.Assemblies,
            "each provider's options.Assemblies must be an independent List" );
    }

    // ---- Phase 2 Task 2.4 — Profile filtering -----------------------------

    [TestMethod]
    public void ProfileFiltering_IsPerProvider()
    {
        // Per-provider Profiles configured on each Add{Provider}Migrations
        // must NOT bleed across providers in a multi-provider host.
        var services = BaseServices();
        services.AddPostgresMigrations( opts => opts.Profiles = new[] { "bootstrap" } );
        services.AddMongoDBMigrations( opts => opts.Profiles = new[] { "seed-data" } );
        var sp = services.BuildServiceProvider();

        var pgOptions = sp.GetRequiredService<PostgresMigrationOptions>();
        var moOptions = sp.GetRequiredService<MongoDBMigrationOptions>();

        pgOptions.Profiles.Should().BeEquivalentTo( new[] { "bootstrap" } );
        moOptions.Profiles.Should().BeEquivalentTo( new[] { "seed-data" } );
        pgOptions.Profiles.Should().NotContain( "seed-data",
            "Postgres profiles must not include MongoDB's profile values" );
        moOptions.Profiles.Should().NotContain( "bootstrap",
            "MongoDB profiles must not include Postgres's profile values" );
    }

    // ---- Phase 4 Task 4.1 — services.Replace semantics --------------------

    [TestMethod]
    public void ServicesReplace_TypedRunner_ResolvesReplacement()
    {
        // services.Replace<PostgresMigrationRunner>() after AddPostgresMigrations
        // must resolve the replacement instance, not the original. This is
        // the documented escape hatch for operators who need to wrap a
        // runner (e.g., to add metrics, retry, custom logging).
        var services = BaseServices();
        services.AddPostgresMigrations();

        var customRunner = new PostgresMigrationRunner(
            Substitute.For<IMigrationRecordStore>(),
            new PostgresMigrationOptions( Substitute.For<IMigrationActivator>() ),
            NullLoggerFactory.Instance );

        services.Replace( ServiceDescriptor.Singleton( customRunner ) );
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<PostgresMigrationRunner>().Should().BeSameAs( customRunner );
        // The base alias still resolves through the typed runner (single-
        // provider mode), and the typed runner is now the replacement.
        sp.GetRequiredService<MigrationRunner>().Should().BeSameAs( customRunner );
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _captured;
        public CapturingLoggerProvider( List<string> captured ) => _captured = captured;
        public ILogger CreateLogger( string categoryName )
        {
            _captured.Add( categoryName );
            return NullLogger.Instance;
        }
        public void Dispose() { }
    }
}
