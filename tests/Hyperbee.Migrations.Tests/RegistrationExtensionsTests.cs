using FluentAssertions;
using Hyperbee.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Tests;

// Multi-runner Phase 1.0 unit coverage: RegisterBaseAliases must install
// the marker + legacy aliases on the FIRST provider, and replace those
// aliases with throwing factories on subsequent providers (per ADR-0023
// amendment F1).

[TestClass]
public class RegistrationExtensionsTests
{
    private static Func<IServiceProvider, MigrationOptions> OptionsFactory( MigrationOptions instance ) => _ => instance;
    private static Func<IServiceProvider, IMigrationRecordStore> StoreFactory( IMigrationRecordStore instance ) => _ => instance;
    private static Func<IServiceProvider, MigrationRunner> RunnerFactory( MigrationRunner instance ) => _ => instance;

    private static MigrationOptions MakeOptions() => new( Substitute.For<IMigrationActivator>() );
    private static IMigrationRecordStore MakeStore() => Substitute.For<IMigrationRecordStore>();
    private static MigrationRunner MakeRunner( IMigrationRecordStore store, MigrationOptions options )
        => new( store, options, NullLoggerFactory.Instance );

    [TestMethod]
    public void FirstProvider_RegistersMarkerAndLegacyAliases()
    {
        var services = new ServiceCollection();
        var opts = MakeOptions();
        var store = MakeStore();
        var runner = MakeRunner( store, opts );

        services.RegisterBaseAliases(
            "Postgres",
            OptionsFactory( opts ),
            StoreFactory( store ),
            RunnerFactory( runner ) );

        var sp = services.BuildServiceProvider();

        sp.GetService<MigrationOptions>().Should().BeSameAs( opts );
        sp.GetService<IMigrationRecordStore>().Should().BeSameAs( store );
        sp.GetService<MigrationRunner>().Should().BeSameAs( runner );

        var marker = sp.GetService<MultiProviderRegistrationMarker>();
        marker.Should().NotBeNull();
        marker.FirstProvider.Should().Be( "Postgres" );
        marker.AllProviders.Should().BeEquivalentTo( new[] { "Postgres" } );
    }

    [TestMethod]
    public void SecondProvider_ReplacesAliasesWithThrowingFactories()
    {
        var services = new ServiceCollection();

        services.RegisterBaseAliases(
            "Postgres",
            OptionsFactory( MakeOptions() ),
            StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );

        services.RegisterBaseAliases(
            "MongoDB",
            OptionsFactory( MakeOptions() ),
            StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );

        var sp = services.BuildServiceProvider();

        var marker = sp.GetService<MultiProviderRegistrationMarker>();
        marker.Should().NotBeNull();
        marker.AllProviders.Should().BeEquivalentTo( new[] { "Postgres", "MongoDB" } );

        Action resolveOptions = () => sp.GetService<MigrationOptions>();
        resolveOptions.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*" )
            .WithMessage( "*MigrationOptions*" );

        Action resolveStore = () => sp.GetService<IMigrationRecordStore>();
        resolveStore.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*" )
            .WithMessage( "*IMigrationRecordStore*" );

        Action resolveRunner = () => sp.GetService<MigrationRunner>();
        resolveRunner.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*" )
            .WithMessage( "*MigrationRunner*" );
    }

    [TestMethod]
    public void ThrowMessage_NamesADRAndDocPath()
    {
        var services = new ServiceCollection();

        services.RegisterBaseAliases(
            "Postgres", OptionsFactory( MakeOptions() ), StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );
        services.RegisterBaseAliases(
            "MongoDB", OptionsFactory( MakeOptions() ), StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );

        var sp = services.BuildServiceProvider();

        try
        {
            sp.GetService<MigrationRunner>();
            Assert.Fail( "Expected InvalidOperationException" );
        }
        catch ( InvalidOperationException ex )
        {
            ex.Message.Should().Contain( "ADR-0023" );
            ex.Message.Should().Contain( "multi-provider-hosts.md" );
            // Names the typed runner an operator should resolve instead.
            ex.Message.Should().Contain( "PostgresMigrationRunner" );
        }
    }

    [TestMethod]
    public void SamProvider_RegisteredTwice_RemainsSingleProviderMode()
    {
        // Idempotent: two calls with the same provider name (e.g., from
        // two helper methods that both call AddPostgresMigrations) must
        // NOT flip into multi-provider mode.
        var services = new ServiceCollection();
        var opts1 = MakeOptions();
        var store1 = MakeStore();
        var runner1 = MakeRunner( store1, opts1 );

        services.RegisterBaseAliases(
            "Postgres", OptionsFactory( opts1 ), StoreFactory( store1 ), RunnerFactory( runner1 ) );
        services.RegisterBaseAliases(
            "Postgres",
            OptionsFactory( MakeOptions() ),
            StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );

        var sp = services.BuildServiceProvider();

        // First registration wins (per AddSingleton's first-wins semantics
        // when the second factory was a duplicate of the first provider
        // name). The marker reflects single-provider mode.
        var marker = sp.GetService<MultiProviderRegistrationMarker>();
        marker.AllProviders.Should().HaveCount( 1 );
        marker.AllProviders.Should().Contain( "Postgres" );

        // Base aliases still resolve cleanly (no throwing factory installed).
        sp.GetService<MigrationOptions>().Should().BeSameAs( opts1 );
        sp.GetService<MigrationRunner>().Should().BeSameAs( runner1 );
    }

    [TestMethod]
    public void ThreeProviders_AllNamedInThrowMessage()
    {
        var services = new ServiceCollection();
        foreach ( var p in new[] { "Postgres", "MongoDB", "Couchbase" } )
        {
            services.RegisterBaseAliases(
                p,
                OptionsFactory( MakeOptions() ),
                StoreFactory( MakeStore() ),
                RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );
        }

        var sp = services.BuildServiceProvider();

        var marker = sp.GetService<MultiProviderRegistrationMarker>();
        marker.AllProviders.Should().BeEquivalentTo( new[] { "Postgres", "MongoDB", "Couchbase" } );

        Action act = () => sp.GetService<MigrationRunner>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*Postgres*MongoDB*Couchbase*" );
    }

    [TestMethod]
    public void UserSuppliedRegistration_Survives_SecondProviderFlip()
    {
        // R-9: a user-supplied MigrationOptions / IMigrationRecordStore /
        // MigrationRunner registration made BEFORE any AddXxxMigrations call
        // must survive a second-provider flip. Previously this scenario
        // wiped the user's registration because RegisterBaseAliases called
        // RemoveAll<T>() instead of removing only the helper-owned descriptors.
        // Classic test-harness footgun: fake store registered first, real
        // provider added second, fake silently disappeared.
        //
        // Note: in multi-provider mode the throwing factory poisons base-type
        // RESOLUTION by design (operators must resolve typed runners). R-9's
        // guarantee is "your ServiceDescriptor is not destroyed", so the
        // assertion is at the descriptor level rather than via the provider.
        var services = new ServiceCollection();

        var userOpts = MakeOptions();
        var userStore = MakeStore();
        var userRunner = MakeRunner( userStore, userOpts );
        services.AddSingleton( userOpts );
        services.AddSingleton( userStore );
        services.AddSingleton( userRunner );

        services.RegisterBaseAliases(
            "Postgres", OptionsFactory( MakeOptions() ), StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );
        services.RegisterBaseAliases(
            "MongoDB", OptionsFactory( MakeOptions() ), StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );

        // User-supplied descriptors survive at the ServiceCollection level.
        services.Should().ContainSingle( d =>
            d.ServiceType == typeof( MigrationOptions ) && ReferenceEquals( d.ImplementationInstance, userOpts ) );
        services.Should().ContainSingle( d =>
            d.ServiceType == typeof( IMigrationRecordStore ) && ReferenceEquals( d.ImplementationInstance, userStore ) );
        services.Should().ContainSingle( d =>
            d.ServiceType == typeof( MigrationRunner ) && ReferenceEquals( d.ImplementationInstance, userRunner ) );

        // And the throwing factory is the LAST registration for each base
        // type, so DI's last-wins semantics route a plain GetService<T>() to
        // the throw (multi-provider safety, ADR-0023 F1).
        var sp = services.BuildServiceProvider();
        Action resolveOptions = () => sp.GetService<MigrationOptions>();
        resolveOptions.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void UserSuppliedRegistration_Survives_SingleProviderRegistration()
    {
        // R-9: even when only one provider registers (no flip), a pre-existing
        // user-supplied registration must remain enumerable and recoverable --
        // the helper appends its own alias rather than replacing what the user
        // installed. In single-provider mode no throwing factory exists, so
        // GetServices<T>() enumerates safely.
        var services = new ServiceCollection();

        var userOpts = MakeOptions();
        services.AddSingleton( userOpts );

        services.RegisterBaseAliases(
            "Postgres", OptionsFactory( MakeOptions() ), StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );

        var sp = services.BuildServiceProvider();

        sp.GetServices<MigrationOptions>().Should().Contain( userOpts );
    }

    [TestMethod]
    public void NullArgs_Throw()
    {
        var services = new ServiceCollection();
        Action nullName = () => services.RegisterBaseAliases(
            "", OptionsFactory( MakeOptions() ), StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );
        nullName.Should().Throw<ArgumentException>();

        Action nullOpts = () => services.RegisterBaseAliases(
            "Postgres", null, StoreFactory( MakeStore() ),
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );
        nullOpts.Should().Throw<ArgumentNullException>();

        Action nullStore = () => services.RegisterBaseAliases(
            "Postgres", OptionsFactory( MakeOptions() ), null,
            RunnerFactory( MakeRunner( MakeStore(), MakeOptions() ) ) );
        nullStore.Should().Throw<ArgumentNullException>();

        Action nullRunner = () => services.RegisterBaseAliases(
            "Postgres", OptionsFactory( MakeOptions() ), StoreFactory( MakeStore() ), null );
        nullRunner.Should().Throw<ArgumentNullException>();
    }
}

// MigrationRunner ILoggerFactory ctor: ensures the runtime-type categorization
// works when a subclass is constructed via the new ctor (per ADR-0023).
[TestClass]
public class MigrationRunnerLoggerCategoryTests
{
    private sealed class DerivedRunner : MigrationRunner
    {
        public DerivedRunner( IMigrationRecordStore recordStore, MigrationOptions options, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory )
            : base( recordStore, options, loggerFactory )
        {
        }
    }

    [TestMethod]
    public void LoggerFactoryCtor_CategorizesUnderRuntimeType()
    {
        var captured = new List<string>();
        var loggerFactory = Substitute.For<Microsoft.Extensions.Logging.ILoggerFactory>();
        loggerFactory.CreateLogger( Arg.Do<string>( captured.Add ) ).Returns( NullLogger.Instance );

        _ = new DerivedRunner(
            Substitute.For<IMigrationRecordStore>(),
            new MigrationOptions( Substitute.For<IMigrationActivator>() ),
            loggerFactory );

        captured.Should().Contain( c => c.EndsWith( "DerivedRunner" ) );
    }

    [TestMethod]
    public void BackCompatCtor_StillAccepted()
    {
        // The ILogger<MigrationRunner> overload must continue to compile
        // and work for existing call sites.
        var runner = new MigrationRunner(
            Substitute.For<IMigrationRecordStore>(),
            new MigrationOptions( Substitute.For<IMigrationActivator>() ),
            NullLogger<MigrationRunner>.Instance );

        runner.Should().NotBeNull();
    }
}
