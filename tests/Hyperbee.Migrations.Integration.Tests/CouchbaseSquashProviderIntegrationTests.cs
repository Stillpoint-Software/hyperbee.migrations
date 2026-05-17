//#define INTEGRATIONS
using System.Reflection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// End-to-end coverage for CouchbaseSquashProvider per ADR-0024 Week 2.
// Uses an isolated Couchbase container for the live probe so it doesn't
// conflict with the shared CouchbaseTestContainer + CouchbaseRunnerTest.
//
// F-1 closed: IsolatedCouchbaseContainer configures
// setupAlternateAddresses so host-side SDK connections work cleanly.

[TestClass]
[DoNotParallelize]
// LocalOnly: heavy container-based integration test; excluded from the gating CI matrix (does not gate the NuGet publish). Runs locally / on demand.
[TestCategory( "LocalOnly" )]
public class CouchbaseSquashProviderIntegrationTests
{
    private static Assembly _sampleAssembly;
    private static IsolatedCouchbaseContainer _liveProbeContainer;

    [ClassInitialize( InheritanceBehavior.None )]
    public static async Task ClassSetup( TestContext context )
    {
        _sampleAssembly = LoadSampleAssembly();
        _liveProbeContainer = await IsolatedCouchbaseContainer.StartAsync();
    }

    [ClassCleanup( InheritanceBehavior.None )]
    public static async Task ClassCleanup()
    {
        if ( _liveProbeContainer != null )
            await _liveProbeContainer.DisposeAsync();
    }

    [TestMethod]
    public async Task GenerateAsync_AgainstSampleAssembly_ProducesGeneratedResult()
    {
        var provider = new CouchbaseSquashProvider();
        var host = MigrationHostDiscovery.Discover( _sampleAssembly );
        var descriptors = DescribeSampleMigrations( _sampleAssembly );
        Assert.IsTrue( descriptors.Count > 0 );

        var ctx = new SquashRequest
        {
            SquashName = "Squash_CB_CLI_IT",
            FromVersion = 1,
            ToVersion = descriptors[^1].Attribute.Version,
            ConnectionString = _liveProbeContainer.ConnectionString,
            Descriptors = descriptors,
            MigrationHost = host,
            Options = new SquashGenerationOptions
            {
                LowerBound = 1,
                UpperBound = descriptors[^1].Attribute.Version
            },
            ProviderOptions = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
            {
                ["bucket-name"] = _liveProbeContainer.BucketName,
                ["mgmt-port"] = _liveProbeContainer.MgmtPort.ToString( System.Globalization.CultureInfo.InvariantCulture )
            }
        };

        var result = await provider.GenerateAsync( ctx, CancellationToken.None );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}\n{failed.Cause}" );

        var gen = (SquashGenerationResult.Generated) result;
        Assert.IsFalse( string.IsNullOrWhiteSpace( gen.Content ) );
        Assert.AreEqual( ".pql", provider.SquashFileExtension );
        Assert.IsTrue( gen.Topology.Properties.Count > 0 );
    }

    private static Assembly LoadSampleAssembly()
    {
        var testDir = AppContext.BaseDirectory;
        var candidate = Path.Combine( testDir, "Hyperbee.Migrations.Couchbase.Samples.dll" );
        if ( File.Exists( candidate ) )
            return Assembly.LoadFrom( candidate );

        var tfm = Path.GetFileName( testDir.TrimEnd( Path.DirectorySeparatorChar ) );
        var cfg = Path.GetFileName( Path.GetDirectoryName( testDir.TrimEnd( Path.DirectorySeparatorChar ) ) );
        var repoRoot = Path.GetFullPath( Path.Combine( testDir, "..", "..", "..", "..", ".." ) );
        var fallback = Path.Combine( repoRoot,
            "runners", "samples", "Hyperbee.Migrations.Couchbase.Samples", "bin", cfg!, tfm!,
            "Hyperbee.Migrations.Couchbase.Samples.dll" );
        if ( File.Exists( fallback ) )
            return Assembly.LoadFrom( fallback );

        throw new FileNotFoundException(
            $"Could not locate Hyperbee.Migrations.Couchbase.Samples.dll. Looked in `{candidate}` and `{fallback}`." );
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
            list.Add( new MigrationDescriptor( type, attr, Array.Empty<long>() ) );
        }
        return list.OrderBy( d => d.Attribute.Version ).ToArray();
    }
}

#endif
