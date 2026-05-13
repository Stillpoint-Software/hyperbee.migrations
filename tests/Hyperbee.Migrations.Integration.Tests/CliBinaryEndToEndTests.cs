//#define INTEGRATIONS
using System.Diagnostics;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// End-to-end test of the `hyperbee-migrations` CLI binary as a child
// process. Spawns the executable, points it at the Postgres sample
// assembly + a real Postgres container, runs `squash` through to a
// generated artifact on disk, and verifies the artifact contract.
//
// This is the only test that exercises the CLI's PROCESS shape
// (assembly load context, provider discovery, host activation, exit
// codes, argv parsing). All other CLI integration coverage stays at
// the in-process method-call layer.

[TestClass]
[DoNotParallelize]
public class CliBinaryEndToEndTests
{
    private static Testcontainers.PostgreSql.PostgreSqlContainer _liveContainer;
    private static string _liveConnectionString;
    private static string _cliExecutable;
    private static string _samplesAssemblyPath;
    private static string _outputDirectory;

    [ClassInitialize( InheritanceBehavior.None )]
    public static async Task ClassSetup( TestContext context )
    {
        _cliExecutable = LocateCliExecutable();
        _samplesAssemblyPath = LocateSamplesAssembly();

        _liveContainer = new Testcontainers.PostgreSql.PostgreSqlBuilder( "postgres:16-alpine" )
            .WithDatabase( "live" )
            .WithUsername( "live" )
            .WithPassword( "live" )
            .WithCleanUp( true )
            .Build();
        await _liveContainer.StartAsync();
        _liveConnectionString = _liveContainer.GetConnectionString();

        _outputDirectory = Path.Combine( Path.GetTempPath(), "hbm-cli-e2e-" + Guid.NewGuid().ToString( "N" )[..8] );
        Directory.CreateDirectory( _outputDirectory );
    }

    [ClassCleanup( InheritanceBehavior.None )]
    public static async Task ClassCleanup()
    {
        if ( _liveContainer != null )
            await _liveContainer.DisposeAsync();
        if ( _outputDirectory != null && Directory.Exists( _outputDirectory ) )
        {
            try { Directory.Delete( _outputDirectory, recursive: true ); } catch { }
        }
    }

    [TestMethod]
    public async Task SquashVerb_AgainstPostgresSample_ProducesArtifacts()
    {
        // Run: hyperbee-migrations squash --provider postgres
        //          --connection <live container>
        //          --range 1-9999
        //          --output <tmp>
        //          --assembly <samples dll>
        //          --no-scan="reason >= 20 chars for CLI E2E test path"
        //          --no-fleet-manifest="reason >= 20 chars for CLI E2E test path"
        var args = new[]
        {
            "squash",
            "--provider", "postgres",
            "--connection", _liveConnectionString,
            "--range", "1-9999",
            "--output", _outputDirectory,
            "--assembly", _samplesAssemblyPath,
            "--name", "Squash_BinaryE2E",
            "--no-scan=CLI binary E2E test - source not required for this path",
            "--no-fleet-manifest=CLI binary E2E test - manifest not required for this path"
        };

        var (stdout, stderr, exitCode) = await RunCliAsync( args );

        if ( exitCode != 0 )
            Assert.Fail( $"CLI exited {exitCode}.\nSTDOUT:\n{stdout}\n\nSTDERR:\n{stderr}" );

        // Artifact contract: <name>.sql + <name>.metadata.json + <name>.summary.md
        var sqlPath = Path.Combine( _outputDirectory, "Squash_BinaryE2E.sql" );
        var metaPath = Path.Combine( _outputDirectory, "Squash_BinaryE2E.metadata.json" );
        var summaryPath = Path.Combine( _outputDirectory, "Squash_BinaryE2E.summary.md" );

        Assert.IsTrue( File.Exists( sqlPath ), $"Expected SQL artifact at {sqlPath}. STDOUT:\n{stdout}" );
        Assert.IsTrue( File.Exists( metaPath ), $"Expected metadata at {metaPath}." );
        Assert.IsTrue( File.Exists( summaryPath ), $"Expected summary at {summaryPath}." );

        var sqlContent = await File.ReadAllTextAsync( sqlPath );
        Assert.IsFalse( string.IsNullOrWhiteSpace( sqlContent ), "Emitted SQL must be non-empty." );

        var metaContent = await File.ReadAllTextAsync( metaPath );
        StringAssert.Contains( metaContent, "\"ProviderId\"" );
        StringAssert.Contains( metaContent, "\"postgres\"" );
    }

    [TestMethod]
    public async Task SquashVerb_MissingScanSource_ExitsNonZero()
    {
        var args = new[]
        {
            "squash",
            "--provider", "postgres",
            "--connection", _liveConnectionString,
            "--range", "1-9999",
            "--output", _outputDirectory,
            "--assembly", _samplesAssemblyPath
            // No --scan-source, no --no-scan -- R-6 default-deny refusal
        };

        var (_, stderr, exitCode) = await RunCliAsync( args );
        Assert.AreNotEqual( 0, exitCode, "CLI must refuse without --scan-source / --no-scan." );
        StringAssert.Contains( stderr, "--scan-source" );
        StringAssert.Contains( stderr, "ADR-0019 A5" );
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunCliAsync( string[] args )
    {
        var psi = new ProcessStartInfo
        {
            FileName = _cliExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach ( var a in args )
            psi.ArgumentList.Add( a );

        using var process = Process.Start( psi )!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var completed = process.WaitForExit( TimeSpan.FromMinutes( 10 ) );
        if ( !completed )
        {
            try { process.Kill( entireProcessTree: true ); } catch { }
            throw new TimeoutException( "CLI process did not exit within 10 minutes." );
        }

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    private static string LocateCliExecutable()
    {
        var testDir = AppContext.BaseDirectory;
        var tfm = Path.GetFileName( testDir.TrimEnd( Path.DirectorySeparatorChar ) );
        var cfg = Path.GetFileName( Path.GetDirectoryName( testDir.TrimEnd( Path.DirectorySeparatorChar ) ) );
        var repoRoot = Path.GetFullPath( Path.Combine( testDir, "..", "..", "..", "..", ".." ) );

        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var cliPath = Path.Combine( repoRoot,
            "runners", "Hyperbee.Migrations.Cli", "bin", cfg!, tfm!,
            "hyperbee-migrations" + ext );
        if ( !File.Exists( cliPath ) )
            throw new FileNotFoundException(
                $"Could not locate the CLI binary at `{cliPath}`. Build the CLI project before running this test." );
        return cliPath;
    }

    private static string LocateSamplesAssembly()
    {
        var testDir = AppContext.BaseDirectory;
        var tfm = Path.GetFileName( testDir.TrimEnd( Path.DirectorySeparatorChar ) );
        var cfg = Path.GetFileName( Path.GetDirectoryName( testDir.TrimEnd( Path.DirectorySeparatorChar ) ) );
        var repoRoot = Path.GetFullPath( Path.Combine( testDir, "..", "..", "..", "..", ".." ) );

        // The sample build output sits next to the SquashCli package's
        // DLLs (via project-reference build ordering). We use the sample's
        // own bin directory so the SquashCli + IMigrationHost types
        // travel with it on the AssemblyLoadContext probe path.
        var samplesPath = Path.Combine( repoRoot,
            "runners", "samples", "Hyperbee.Migrations.Postgres.Samples", "bin", cfg!, tfm!,
            "Hyperbee.Migrations.Postgres.Samples.dll" );
        if ( !File.Exists( samplesPath ) )
            throw new FileNotFoundException(
                $"Could not locate the Postgres sample assembly at `{samplesPath}`. Build the sample project before running this test." );
        return samplesPath;
    }
}

#endif
