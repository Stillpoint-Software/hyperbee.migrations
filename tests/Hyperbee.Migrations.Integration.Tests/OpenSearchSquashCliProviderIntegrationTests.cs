//#define INTEGRATIONS
using System.Reflection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.OpenSearch.SquashCli;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// End-to-end coverage for OpenSearchSquashCliProvider per ADR-0024 Week 2.

[TestClass]
[DoNotParallelize]
public class OpenSearchSquashCliProviderIntegrationTests
{
    private static Assembly _sampleAssembly;

    [ClassInitialize( InheritanceBehavior.None )]
    public static void ClassSetup( TestContext context )
    {
        _sampleAssembly = LoadSampleAssembly();
    }

    [TestMethod]
    public async Task GenerateAsync_AgainstSampleAssembly_ProducesGeneratedResult()
    {
        var provider = new OpenSearchSquashCliProvider();
        var host = MigrationHostDiscovery.Discover( _sampleAssembly );
        var descriptors = DescribeSampleMigrations( _sampleAssembly );
        Assert.IsTrue( descriptors.Count > 0, "Sample assembly must expose at least one [Migration]." );

        // OpenSearch strategy needs an OpenSearchClient handle bound to the
        // live cluster (topology capture).
        var liveEndpoint = Container.OpenSearch.OpenSearchTestContainer.Endpoint;

        var ctx = new SquashCliContext
        {
            SquashName = "Squash_OS_CLI_IT",
            FromVersion = 1,
            ToVersion = descriptors[^1].Attribute.Version,
            ConnectionString = liveEndpoint.ToString(),
            Descriptors = descriptors,
            MigrationHost = host,
            Options = new SquashGenerationOptions
            {
                LowerBound = 1,
                UpperBound = descriptors[^1].Attribute.Version
            }
        };

        var result = await provider.GenerateAsync( ctx, CancellationToken.None );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}\n{failed.Cause}" );

        var gen = (SquashGenerationResult.Generated) result;
        Assert.IsFalse( string.IsNullOrWhiteSpace( gen.Content ), "Generated squash content must be non-empty." );
        Assert.AreEqual( ".statements", provider.SquashFileExtension );
        Assert.IsTrue( gen.Topology.Properties.Count > 0 );
    }

    private static Assembly LoadSampleAssembly()
    {
        var testDir = AppContext.BaseDirectory;
        var candidate = Path.Combine( testDir, "Hyperbee.Migrations.OpenSearch.Samples.dll" );
        if ( File.Exists( candidate ) )
            return Assembly.LoadFrom( candidate );

        var tfm = Path.GetFileName( testDir.TrimEnd( Path.DirectorySeparatorChar ) );
        var cfg = Path.GetFileName( Path.GetDirectoryName( testDir.TrimEnd( Path.DirectorySeparatorChar ) ) );
        var repoRoot = Path.GetFullPath( Path.Combine( testDir, "..", "..", "..", "..", ".." ) );
        var fallback = Path.Combine( repoRoot,
            "runners", "samples", "Hyperbee.Migrations.OpenSearch.Samples", "bin", cfg!, tfm!,
            "Hyperbee.Migrations.OpenSearch.Samples.dll" );
        if ( File.Exists( fallback ) )
            return Assembly.LoadFrom( fallback );

        throw new FileNotFoundException(
            $"Could not locate Hyperbee.Migrations.OpenSearch.Samples.dll. Looked in `{candidate}` and `{fallback}`." );
    }

    private static IReadOnlyList<MigrationDescriptor> DescribeSampleMigrations( Assembly assembly )
    {
        var list = new List<MigrationDescriptor>();
        foreach ( var type in assembly.GetTypes() )
        {
            if ( !typeof( Migration ).IsAssignableFrom( type ) || type.IsAbstract )
                continue;
            var attr = type.GetCustomAttributes( typeof( MigrationAttribute ), inherit: false )
                .Cast<MigrationAttribute>()
                .FirstOrDefault();
            if ( attr == null )
                continue;
            // Squash codegen captures structural state. `journal: false`
            // migrations are ongoing-reconciliation steps that run against
            // populated clusters (e.g., sample 9001 re-applies an ISM
            // policy to a `*-suffixed` index pattern that has no matches
            // on a fresh ephemeral cluster). They are not part of the
            // schema baseline a squash subsumes, so exclude them from
            // the descriptor set.
            if ( !attr.Journal )
                continue;
            list.Add( new MigrationDescriptor( type, attr, Array.Empty<long>() ) );
        }
        return list.OrderBy( d => d.Attribute.Version ).ToArray();
    }
}

#endif
