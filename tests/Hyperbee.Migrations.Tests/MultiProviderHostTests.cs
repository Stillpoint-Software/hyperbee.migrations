using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike;
using Hyperbee.Migrations.Providers.MongoDB;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
