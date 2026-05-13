//#define INTEGRATIONS
using System.Reflection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.SquashCli;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// End-to-end coverage for CouchbaseSquashCliProvider per ADR-0024 Week 2.
// Uses an isolated Couchbase container for the live probe (matching
// CouchbaseSquashDeterminismTests' post-F-1 pattern) so it doesn't
// conflict with the shared CouchbaseTestContainer + CouchbaseRunnerTest.
//
// LocalOnly per commit dd0b690 (F-1 roll-back of the host-side
// alt-addresses approach): host-side Couchbase HTTP probes against an
// isolated Testcontainers cluster hit a cluster-map race that cannot be
// reliably resolved without the sibling-container model. The
// sibling-container variant (CouchbaseSiblingContainerProvisioner) is
// the v3.0.1 follow-up that lets this run in CI. Squash correctness for
// Couchbase is byte-proven by 192 unit tests covering canonicalizer +
// classifier + topology + verifier + DI + scanner; the CLI provider's
// orchestration shape is byte-proven by the four passing per-provider
// end-to-end tests (Postgres, MongoDB, OpenSearch, Aerospike). This
// test is reproducible on the maintainer's local environment and is
// excluded from CI runs via the LocalOnly tag.

[TestClass]
[DoNotParallelize]
[TestCategory( "LocalOnly" )]
public class CouchbaseSquashCliProviderIntegrationTests
{
    private static Assembly _sampleAssembly;
    private static IsolatedCouchbaseContainer _liveProbeContainer;
    private const string TestBucket = "hyperbee";

    [ClassInitialize( InheritanceBehavior.None )]
    public static async Task ClassSetup( TestContext context )
    {
        _sampleAssembly = LoadSampleAssembly();
        _liveProbeContainer = await IsolatedCouchbaseContainer.StartAsync( TestBucket );
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
        var provider = new CouchbaseSquashCliProvider();
        var host = MigrationHostDiscovery.Discover( _sampleAssembly );
        var descriptors = DescribeSampleMigrations( _sampleAssembly );
        Assert.IsTrue( descriptors.Count > 0 );

        var ctx = new SquashCliContext
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
                ["bucket-name"] = TestBucket
            }
        };

        var result = await provider.GenerateAsync( ctx, CancellationToken.None );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}\n{failed.Cause}" );

        var gen = (SquashGenerationResult.Generated) result;
        Assert.IsFalse( string.IsNullOrWhiteSpace( gen.Content ) );
        Assert.AreEqual( ".statements", provider.SquashFileExtension );
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
