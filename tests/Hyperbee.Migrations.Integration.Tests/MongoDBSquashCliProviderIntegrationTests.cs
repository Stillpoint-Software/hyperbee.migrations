//#define INTEGRATIONS
using System.Reflection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.MongoDB.SquashCli;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// End-to-end coverage for MongoDBSquashCliProvider per ADR-0024 Week 2.

[TestClass]
[DoNotParallelize]
public class MongoDBSquashCliProviderIntegrationTests
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
        var provider = new MongoDBSquashCliProvider();
        var host = MigrationHostDiscovery.Discover( _sampleAssembly );
        var descriptors = DescribeSampleMigrations( _sampleAssembly );
        Assert.IsTrue( descriptors.Count > 0 );

        // MongoDB live probe uses the shared test container.
        var liveConn = Container.MongoDb.MongoDbTestContainer.ConnectionString
            ?? throw new InvalidOperationException( "MongoDbTestContainer not initialized." );

        var ctx = new SquashCliContext
        {
            SquashName = "Squash_Mongo_CLI_IT",
            FromVersion = 1,
            ToVersion = descriptors[^1].Attribute.Version,
            ConnectionString = liveConn,
            Descriptors = descriptors,
            MigrationHost = host,
            Options = new SquashGenerationOptions
            {
                LowerBound = 1,
                UpperBound = descriptors[^1].Attribute.Version
            },
            ProviderOptions = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
            {
                ["database-name"] = "test"
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
        var candidate = Path.Combine( testDir, "Hyperbee.Migrations.MongoDB.Samples.dll" );
        if ( File.Exists( candidate ) )
            return Assembly.LoadFrom( candidate );

        var tfm = Path.GetFileName( testDir.TrimEnd( Path.DirectorySeparatorChar ) );
        var cfg = Path.GetFileName( Path.GetDirectoryName( testDir.TrimEnd( Path.DirectorySeparatorChar ) ) );
        var repoRoot = Path.GetFullPath( Path.Combine( testDir, "..", "..", "..", "..", ".." ) );
        var fallback = Path.Combine( repoRoot,
            "runners", "samples", "Hyperbee.Migrations.MongoDB.Samples", "bin", cfg!, tfm!,
            "Hyperbee.Migrations.MongoDB.Samples.dll" );
        if ( File.Exists( fallback ) )
            return Assembly.LoadFrom( fallback );

        throw new FileNotFoundException(
            $"Could not locate Hyperbee.Migrations.MongoDB.Samples.dll. Looked in `{candidate}` and `{fallback}`." );
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
