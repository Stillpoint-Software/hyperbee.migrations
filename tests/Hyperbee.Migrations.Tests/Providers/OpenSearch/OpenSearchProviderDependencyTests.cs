#nullable enable
using System.Linq;
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// ADR-0016 — no file-level templating in the OpenSearch provider.
//
// The provider intentionally has no Hyperbee.Templating dependency; per-env
// variation goes through typed options + IConfiguration binding instead.
// This test asserts the absence as a guard against future drift: if a
// contributor adds Hyperbee.Templating to the provider csproj, this test
// fails before merge.

[TestClass]
public class OpenSearchProviderDependencyTests
{
    [TestMethod]
    public void Provider_DoesNotReference_HyperbeeTemplating()
    {
        var providerAssembly = typeof( OpenSearchMigrationOptions ).Assembly;
        var referenced = providerAssembly.GetReferencedAssemblies()
            .Select( a => a.Name )
            .ToArray();

        referenced.Should().NotContain(
            name => name != null && name.StartsWith( "Hyperbee.Templating", System.StringComparison.OrdinalIgnoreCase ),
            because: "ADR-0016: the OpenSearch provider intentionally has no file-level templating; environment variation goes through typed options." );
    }
}
