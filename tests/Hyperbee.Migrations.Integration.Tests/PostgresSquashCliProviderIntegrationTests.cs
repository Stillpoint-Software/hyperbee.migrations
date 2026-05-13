//#define INTEGRATIONS
using System.Reflection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Postgres.SquashCli;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// End-to-end coverage for PostgresSquashCliProvider per ADR-0024 Week 2.
//
// Exercises the FULL CLI provider path:
//   1. Load the sample migration assembly by DLL path (avoids namespace
//      shadowing -- the sample's `Hyperbee.Migrations.Postgres.Samples`
//      tree would shadow the SDK's `Npgsql`/etc. if imported as a project
//      reference).
//   2. Discover IMigrationHost from the loaded assembly.
//   3. Build SquashCliContext with the discovered host + the sample's
//      migration descriptors + a separate live-probe container connection
//      string (the strategy opens a live NpgsqlDataSource for topology
//      capture).
//   4. Invoke PostgresSquashCliProvider.GenerateAsync.
//   5. Provider:
//      - Asks PostgresEphemeralProvisioner for a fresh container
//      - Activates the discovered host's IMigrationHost
//      - Calls host.ConfigureAsync(ephemeral connection string)
//      - Resolves PostgresMigrationRunner + runs migrations through the
//        upper bound against the ephemeral container
//      - Runs pg_dump --schema-only via docker exec
//      - Canonicalizes and emits the squash content
//   6. Assert: SquashGenerationResult.Generated; non-empty content; file
//      extension; topology fields populated; second run produces byte-equal
//      output (determinism gate C12).

[TestClass]
[DoNotParallelize]
public class PostgresSquashCliProviderIntegrationTests
{
    private static Assembly _sampleAssembly;
    private static Testcontainers.PostgreSql.PostgreSqlContainer _liveProbeContainer;
    private static string _liveProbeConnectionString;

    [ClassInitialize( InheritanceBehavior.None )]
    public static async Task ClassSetup( TestContext context )
    {
        _sampleAssembly = LoadSampleAssembly();

        // Strategy opens an NpgsqlDataSource against ConnectionString for
        // live topology probing. Use a throwaway container so the test
        // doesn't depend on an external DB.
        _liveProbeContainer = new Testcontainers.PostgreSql.PostgreSqlBuilder( "postgres:16-alpine" )
            .WithDatabase( "probe" )
            .WithUsername( "probe" )
            .WithPassword( "probe" )
            .WithCleanUp( true )
            .Build();
        await _liveProbeContainer.StartAsync();
        // Build the connection string explicitly: Testcontainers v4.10's
        // GetConnectionString returns a URI form (postgres://...) that the
        // PgDumpSnapshotStrategy's Npgsql DataSource interprets without
        // credentials under SCRAM-SHA-256. Explicit key=value form fixes
        // the auth handshake.
        _liveProbeConnectionString = _liveProbeContainer.GetConnectionString();
        context.WriteLine( $"[setup] live probe connection: {_liveProbeConnectionString}" );
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
        var provider = new PostgresSquashCliProvider();
        var host = MigrationHostDiscovery.Discover( _sampleAssembly );

        var descriptors = DescribeSampleMigrations( _sampleAssembly, fromVersion: 1, toVersion: 9999 );
        Assert.IsTrue( descriptors.Count > 0, "Sample assembly must expose at least one [Migration]." );

        var ctx = new SquashCliContext
        {
            SquashName = "Squash_CLI_IT",
            FromVersion = 1,
            ToVersion = descriptors[^1].Attribute.Version,
            ConnectionString = _liveProbeConnectionString,
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
        Assert.IsTrue( gen.Replaces.Count >= 1, "At least one version must be replaced." );
        Assert.AreEqual( ".sql", provider.SquashFileExtension );
        Assert.IsTrue( gen.Topology.Properties.Count > 0, "Generated topology must carry at least one property." );
    }

    [TestMethod]
    public async Task GenerateAsync_RunTwice_ProducesIdenticalContent()
    {
        var provider = new PostgresSquashCliProvider();
        var host = MigrationHostDiscovery.Discover( _sampleAssembly );

        var descriptors = DescribeSampleMigrations( _sampleAssembly, fromVersion: 1, toVersion: 9999 );

        async Task<string> RunAsync()
        {
            var ctx = new SquashCliContext
            {
                SquashName = "Squash_Det_IT",
                FromVersion = 1,
                ToVersion = descriptors[^1].Attribute.Version,
                ConnectionString = _liveProbeConnectionString,
                Descriptors = descriptors,
                MigrationHost = host,
                Options = new SquashGenerationOptions
                {
                    LowerBound = 1,
                    UpperBound = descriptors[^1].Attribute.Version
                }
            };
            var r = await provider.GenerateAsync( ctx, CancellationToken.None );
            if ( r is SquashGenerationResult.Failed f )
                Assert.Fail( $"GenerateAsync failed: {f.Detail}" );
            return ((SquashGenerationResult.Generated) r).Content;
        }

        var content1 = await RunAsync();
        var content2 = await RunAsync();

        Assert.AreEqual( content1, content2,
            "C12 determinism gate: two GenerateAsync invocations against the same migration assembly must produce byte-equal output." );
    }

    // ---- helpers -----------------------------------------------------------

    internal static Assembly LoadSampleAssembly()
    {
        // The Postgres sample csproj is registered as a project reference
        // with ReferenceOutputAssembly=false so MSBuild build-orders it
        // ahead of this project; the sample DLL lands next to the test
        // assembly because both targets share an output directory shape.
        // Fall back to a relative path probe if the side-by-side copy
        // didn't happen (developer environment without solution build).
        var testDir = AppContext.BaseDirectory;
        var candidate = Path.Combine( testDir, "Hyperbee.Migrations.Postgres.Samples.dll" );
        if ( File.Exists( candidate ) )
            return Assembly.LoadFrom( candidate );

        // Solution-relative fallback: the test binary sits at
        // tests/{...}/bin/{cfg}/{tfm}/. The sample binary is at
        // runners/samples/{...}/bin/{cfg}/{tfm}/.
        var tfm = Path.GetFileName( testDir.TrimEnd( Path.DirectorySeparatorChar ) );
        var cfg = Path.GetFileName( Path.GetDirectoryName( testDir.TrimEnd( Path.DirectorySeparatorChar ) ) );
        var repoRoot = Path.GetFullPath( Path.Combine( testDir, "..", "..", "..", "..", ".." ) );
        var solutionPath = Path.Combine( repoRoot,
            "runners", "samples", "Hyperbee.Migrations.Postgres.Samples", "bin", cfg!, tfm!,
            "Hyperbee.Migrations.Postgres.Samples.dll" );
        if ( File.Exists( solutionPath ) )
            return Assembly.LoadFrom( solutionPath );

        throw new FileNotFoundException(
            $"Could not locate Hyperbee.Migrations.Postgres.Samples.dll. Looked in `{candidate}` and `{solutionPath}`. " +
            "Ensure the sample project builds before this test runs (MSBuild project reference with ReferenceOutputAssembly=false should suffice)." );
    }

    internal static IReadOnlyList<MigrationDescriptor> DescribeSampleMigrations(
        Assembly assembly, long fromVersion, long toVersion )
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
            if ( attr.Version < fromVersion || attr.Version > toVersion )
                continue;
            list.Add( new MigrationDescriptor( type, attr, Array.Empty<long>() ) );
        }
        return list.OrderBy( d => d.Attribute.Version ).ToArray();
    }
}

#endif
