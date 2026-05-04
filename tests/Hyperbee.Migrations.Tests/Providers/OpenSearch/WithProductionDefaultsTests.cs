#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// ADR-0012 - WithProductionDefaults() options-factory wiring.
//
// Asserts the four documented defaults flip when the marker is registered,
// and that explicit user configuration still wins (so the call chain
// `services.WithProductionDefaults().AddOpenSearchMigrations(o => o.WaitMode = WaitMode.Off)`
// honors the user's override).

[TestClass]
public class WithProductionDefaultsTests
{
    [TestMethod]
    public void WithoutMarker_OptionsKeepLibraryDefaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddOpenSearchMigrations();

        var options = services.BuildServiceProvider().GetRequiredService<OpenSearchMigrationOptions>();

        options.ClusterHealthThreshold.Should().Be( ClusterHealthThreshold.Yellow );
        options.WaitMode.Should().Be( WaitMode.PerStatement );
        options.RequireUnsafeJustification.Should().BeFalse();
        options.ContextResolutionPolicy.Should().Be( ContextResolutionPolicy.SkipIfUnset );
    }

    [TestMethod]
    public void WithMarker_FlipsAllFourDefaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.WithProductionDefaults();
        services.AddOpenSearchMigrations();

        var options = services.BuildServiceProvider().GetRequiredService<OpenSearchMigrationOptions>();

        options.ClusterHealthThreshold.Should().Be( ClusterHealthThreshold.Green );
        options.WaitMode.Should().Be( WaitMode.PerMigration );
        options.RequireUnsafeJustification.Should().BeTrue();
        options.ContextResolutionPolicy.Should().Be( ContextResolutionPolicy.RequireExplicit );
    }

    [TestMethod]
    public void WithMarker_UserConfigurationStillWins()
    {
        // Production defaults apply first; explicit per-option setting in
        // the configuration callback overrides. This is the documented
        // contract: production-defaults is a forcing function, not a lockout.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.WithProductionDefaults();
        services.AddOpenSearchMigrations( o =>
        {
            o.WaitMode = WaitMode.Off;
            o.ContextResolutionPolicy = ContextResolutionPolicy.SkipIfUnset;
        } );

        var options = services.BuildServiceProvider().GetRequiredService<OpenSearchMigrationOptions>();

        options.WaitMode.Should().Be( WaitMode.Off, because: "user override beats production default" );
        options.ContextResolutionPolicy.Should().Be( ContextResolutionPolicy.SkipIfUnset, because: "user override beats production default" );
        options.ClusterHealthThreshold.Should().Be( ClusterHealthThreshold.Green, because: "production default still applies for non-overridden options" );
        options.RequireUnsafeJustification.Should().BeTrue( because: "production default still applies for non-overridden options" );
    }
}
