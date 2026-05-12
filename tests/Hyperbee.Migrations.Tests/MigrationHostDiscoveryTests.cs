using System.Reflection;
using FluentAssertions;
using Hyperbee.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests;

// ADR-0024 coverage: MigrationHostDiscovery.Discover finds exactly one
// public, non-abstract, default-constructible IMigrationHost in a migration
// assembly. Refuses with operator-actionable detail for zero / multiple
// candidates. TryDiscover + EnumerateCandidates are diagnostic helpers.
//
// Host fixtures are top-level types in this file (not nested) because
// Type.IsPublic returns false for nested public types -- the discovery
// correctly rejects nested types to match real user code conventions
// (BillingMigrationsHost is a top-level public class).

[TestClass]
public class MigrationHostDiscoveryTests
{
    // ---- happy path -------------------------------------------------------

    [TestMethod]
    public void Discover_ReturnsTheHost_WhenExactlyOneCandidateExists()
    {
        var asm = BuildSyntheticAssembly( typeof( ValidSoleHost ) );

        var host = MigrationHostDiscovery.Discover( asm );

        host.Should().NotBeNull();
        host.Should().BeAssignableTo<IMigrationHost>();
    }

    // ---- zero candidates --------------------------------------------------

    [TestMethod]
    public void Discover_Throws_WhenNoCandidateExists()
    {
        var asm = BuildSyntheticAssembly();

        var act = () => MigrationHostDiscovery.Discover( asm );

        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*does not contain*IMigrationHost*" )
            .WithMessage( "*docs/site/cli.md*" );
    }

    // ---- multiple candidates ----------------------------------------------

    [TestMethod]
    public void Discover_Throws_WhenMultipleCandidatesExist()
    {
        var asm = BuildSyntheticAssembly( typeof( FirstHost ), typeof( SecondHost ) );

        var act = () => MigrationHostDiscovery.Discover( asm );

        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*multiple*IMigrationHost*implementations*" )
            .WithMessage( "*Combine them*single host*" );
    }

    // ---- ineligible types -------------------------------------------------

    [TestMethod]
    public void Discover_IgnoresAbstractImplementations()
    {
        var asm = BuildSyntheticAssembly( typeof( AbstractHost ) );

        var act = () => MigrationHostDiscovery.Discover( asm );

        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*does not contain*" );
    }

    [TestMethod]
    public void Discover_IgnoresHostsWithoutDefaultConstructor()
    {
        var asm = BuildSyntheticAssembly( typeof( HostWithoutDefaultCtor ) );

        var act = () => MigrationHostDiscovery.Discover( asm );

        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*does not contain*" );
    }

    [TestMethod]
    public void Discover_IgnoresInternalHosts()
    {
        var asm = BuildSyntheticAssembly( typeof( InternalHost ) );

        var act = () => MigrationHostDiscovery.Discover( asm );

        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*does not contain*" );
    }

    // ---- guard rails ------------------------------------------------------

    [TestMethod]
    public void Discover_Throws_OnNullAssembly()
    {
        var act = () => MigrationHostDiscovery.Discover( null! );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "migrationAssembly" );
    }

    // ---- TryDiscover diagnostic helper -----------------------------------

    [TestMethod]
    public void TryDiscover_ReturnsTrue_WhenSingleCandidateExists()
    {
        var asm = BuildSyntheticAssembly( typeof( ValidSoleHost ) );

        var found = MigrationHostDiscovery.TryDiscover( asm, out var host );

        found.Should().BeTrue();
        host.Should().NotBeNull();
    }

    [TestMethod]
    public void TryDiscover_ReturnsFalse_WhenNoCandidate()
    {
        var asm = BuildSyntheticAssembly();

        var found = MigrationHostDiscovery.TryDiscover( asm, out var host );

        found.Should().BeFalse();
        host.Should().BeNull();
    }

    [TestMethod]
    public void TryDiscover_ReturnsFalse_WhenMultipleCandidates()
    {
        var asm = BuildSyntheticAssembly( typeof( FirstHost ), typeof( SecondHost ) );

        var found = MigrationHostDiscovery.TryDiscover( asm, out var host );

        found.Should().BeFalse();
        host.Should().BeNull();
    }

    // ---- EnumerateCandidates ----------------------------------------------

    [TestMethod]
    public void EnumerateCandidates_ReturnsAllValidImplementations()
    {
        var asm = BuildSyntheticAssembly( typeof( FirstHost ), typeof( SecondHost ) );

        var candidates = MigrationHostDiscovery.EnumerateCandidates( asm );

        candidates.Should().HaveCount( 2 );
        candidates.Should().Contain( typeof( FirstHost ) );
        candidates.Should().Contain( typeof( SecondHost ) );
    }

    [TestMethod]
    public void EnumerateCandidates_ReturnsEmpty_WhenNoCandidates()
    {
        var asm = BuildSyntheticAssembly();

        MigrationHostDiscovery.EnumerateCandidates( asm ).Should().BeEmpty();
    }

    // ---- MigrationHostContext shape --------------------------------------

    [TestMethod]
    public void MigrationHostContext_AllowsMinimalConstruction()
    {
        var ctx = new MigrationHostContext( "Host=localhost" );

        ctx.ConnectionString.Should().Be( "Host=localhost" );
        ctx.OverrideOptions.Should().BeNull();
        ctx.ProviderHints.Should().BeNull();
    }

    [TestMethod]
    public void MigrationHostContext_OverrideOptions_IsHonored()
    {
        Action<MigrationOptions> overrider = _ => { };
        var ctx = new MigrationHostContext( "Host=localhost", overrider );

        ctx.OverrideOptions.Should().BeSameAs( overrider );
    }

    [TestMethod]
    public void MigrationHostContext_ProviderHints_RoundTrip()
    {
        var hints = new Dictionary<string, string> { ["my-key"] = "my-value" };
        var ctx = new MigrationHostContext( "Host=localhost", ProviderHints: hints );

        ctx.ProviderHints.Should().BeSameAs( hints );
    }

    // ---- IMigrationHost contract round-trip via discovery ----------------

    [TestMethod]
    public async Task DiscoveredHost_ConfigureAsync_RoundTripsContext()
    {
        var asm = BuildSyntheticAssembly( typeof( ValidSoleHost ) );
        var host = MigrationHostDiscovery.Discover( asm );

        var ctx = new MigrationHostContext( "Host=localhost;Database=x" );
        var sp = await host.ConfigureAsync( ctx, CancellationToken.None );

        sp.Should().NotBeNull();
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Builds a synthetic in-memory assembly exposing exactly the supplied
    /// types via GetTypes() -- isolates each discovery test from the rest
    /// of the test project's candidate set.
    /// </summary>
    private static Assembly BuildSyntheticAssembly( params Type[] types )
        => new TypeFilteredAssembly( types );

    private sealed class TypeFilteredAssembly : Assembly
    {
        private readonly Type[] _types;
        private readonly AssemblyName _name = new( "Synthetic.Test.Assembly" );

        public TypeFilteredAssembly( Type[] types ) => _types = types;

        public override AssemblyName GetName() => _name;
        public override Type[] GetTypes() => _types;
    }
}

// ---- Test fixture types (top-level public, matching real user-code shape) ---

public sealed class ValidSoleHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync( MigrationHostContext ctx, CancellationToken ct )
        => Task.FromResult<IServiceProvider>( new ServiceCollection().BuildServiceProvider() );
}

public sealed class FirstHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync( MigrationHostContext ctx, CancellationToken ct )
        => Task.FromResult<IServiceProvider>( new ServiceCollection().BuildServiceProvider() );
}

public sealed class SecondHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync( MigrationHostContext ctx, CancellationToken ct )
        => Task.FromResult<IServiceProvider>( new ServiceCollection().BuildServiceProvider() );
}

public abstract class AbstractHost : IMigrationHost
{
    public abstract Task<IServiceProvider> ConfigureAsync( MigrationHostContext ctx, CancellationToken ct );
}

public sealed class HostWithoutDefaultCtor : IMigrationHost
{
    public HostWithoutDefaultCtor( int someDependency ) { _ = someDependency; }
    public Task<IServiceProvider> ConfigureAsync( MigrationHostContext ctx, CancellationToken ct )
        => Task.FromResult<IServiceProvider>( new ServiceCollection().BuildServiceProvider() );
}

internal sealed class InternalHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync( MigrationHostContext ctx, CancellationToken ct )
        => Task.FromResult<IServiceProvider>( new ServiceCollection().BuildServiceProvider() );
}
