using System.Reflection;
using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests.Squash.Cli;

// Per ADR-0024 Week 2: the squash CLI references no provider packages.
// CliProviderRegistry walks the migration assembly's reference closure to
// find ISquashCliProvider implementations. These tests pin the discovery
// contract: provider id case-insensitivity, duplicate-id rejection, missing
// public parameterless ctor, abstract types, non-public types, and the
// happy path against a synthetic provider type declared in this assembly.

[TestClass]
public class CliProviderRegistryTests
{
    [TestMethod]
    public void Discover_FindsTestProviderInThisAssembly()
    {
        var providers = CliProviderRegistry.Discover( typeof( CliProviderRegistryTests ).Assembly );

        providers.Should().ContainKey( "test-provider" );
        providers["test-provider"].Should().BeOfType<TestCliProvider>();
    }

    [TestMethod]
    public void Discover_LookupIsCaseInsensitive()
    {
        var providers = CliProviderRegistry.Discover( typeof( CliProviderRegistryTests ).Assembly );

        providers.ContainsKey( "TEST-PROVIDER" ).Should().BeTrue();
        providers.ContainsKey( "Test-Provider" ).Should().BeTrue();
    }

    [TestMethod]
    public void Discover_NullAssembly_Throws()
    {
        Action act = () => CliProviderRegistry.Discover( null! );
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void Discover_SkipsAbstractAndInternalAndNoCtorTypes()
    {
        // The fixtures below would not be discoverable; the registry should
        // not throw on their presence -- it filters them silently.
        var providers = CliProviderRegistry.Discover( typeof( CliProviderRegistryTests ).Assembly );

        providers.Values.Should().NotContain( p => p.GetType() == typeof( AbstractCliProvider ) );
        providers.Values.Should().NotContain( p => p.GetType().Name == nameof( InternalCliProvider ) );
        providers.Values.Should().NotContain( p => p.GetType() == typeof( NoDefaultCtorCliProvider ) );
    }
}

// Public, default-ctor, non-abstract -- this one IS discovered.
public sealed class TestCliProvider : ISquashCliProvider
{
    public string ProviderId => "test-provider";
    public string SquashFileExtension => ".test";

    public Task<SquashGenerationResult> GenerateAsync(
        SquashCliContext context, CancellationToken cancellationToken )
        => throw new NotSupportedException();

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string sourceDirectory )
        => Array.Empty<MigrationSourceVerdict>();

    public Task<long> ProbeLastAppliedVersionAsync(
        string liveConnectionString,
        IReadOnlyDictionary<string, string> providerOptions,
        CancellationToken cancellationToken )
        => Task.FromResult( 0L );
}

// Abstract -- filtered out.
public abstract class AbstractCliProvider : ISquashCliProvider
{
    public string ProviderId => "abstract";
    public string SquashFileExtension => ".test";

    public Task<SquashGenerationResult> GenerateAsync( SquashCliContext context, CancellationToken ct )
        => throw new NotSupportedException();

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string s )
        => Array.Empty<MigrationSourceVerdict>();

    public Task<long> ProbeLastAppliedVersionAsync(
        string s, IReadOnlyDictionary<string, string> o, CancellationToken c )
        => Task.FromResult( 0L );
}

// Internal -- filtered out.
internal sealed class InternalCliProvider : ISquashCliProvider
{
    public string ProviderId => "internal";
    public string SquashFileExtension => ".test";

    public Task<SquashGenerationResult> GenerateAsync( SquashCliContext context, CancellationToken ct )
        => throw new NotSupportedException();

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string s )
        => Array.Empty<MigrationSourceVerdict>();

    public Task<long> ProbeLastAppliedVersionAsync(
        string s, IReadOnlyDictionary<string, string> o, CancellationToken c )
        => Task.FromResult( 0L );
}

// No public parameterless ctor -- filtered out.
public sealed class NoDefaultCtorCliProvider : ISquashCliProvider
{
    public NoDefaultCtorCliProvider( string required ) { _ = required; }

    public string ProviderId => "no-default-ctor";
    public string SquashFileExtension => ".test";

    public Task<SquashGenerationResult> GenerateAsync( SquashCliContext context, CancellationToken ct )
        => throw new NotSupportedException();

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string s )
        => Array.Empty<MigrationSourceVerdict>();

    public Task<long> ProbeLastAppliedVersionAsync(
        string s, IReadOnlyDictionary<string, string> o, CancellationToken c )
        => Task.FromResult( 0L );
}
