using FluentAssertions;
using Hyperbee.Migrations.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Cli.Tests;

// Smoke tests for Program.Main verb dispatch + help / version paths. Closes
// R-P9 (CLI integration coverage). Driving Program.Main directly avoids the
// cost of a real binary smoke test while still exercising the verb router.

[TestClass]
[DoNotParallelize] // serialized because tests redirect Console
public class ProgramSmokeTests
{
    [TestMethod]
    public async Task Main_NoArgs_PrintsHelpAndReturnsZero()
    {
        var (stdout, _, exit) = await RunAsync( Array.Empty<string>() );
        exit.Should().Be( 0 );
        stdout.Should().Contain( "Hyperbee Migrations CLI" );
        stdout.Should().Contain( "squash" );
        stdout.Should().Contain( "recover" );
        stdout.Should().Contain( "version" );
    }

    [TestMethod]
    public async Task Main_HelpFlag_PrintsHelp()
    {
        foreach ( var helpArg in new[] { "--help", "-h", "help" } )
        {
            var (stdout, _, exit) = await RunAsync( new[] { helpArg } );
            exit.Should().Be( 0, $"--{helpArg} should return 0" );
            stdout.Should().Contain( "Hyperbee Migrations CLI" );
        }
    }

    [TestMethod]
    public async Task Main_VersionVerb_PrintsVersion()
    {
        var (stdout, _, exit) = await RunAsync( new[] { "version" } );
        exit.Should().Be( 0 );
        stdout.Should().Contain( "hyperbee-migrations" );
    }

    [TestMethod]
    public async Task Main_VersionFlag_PrintsVersion()
    {
        var (stdout, _, exit) = await RunAsync( new[] { "--version" } );
        exit.Should().Be( 0 );
        stdout.Should().Contain( "hyperbee-migrations" );
    }

    [TestMethod]
    public async Task Main_UnknownVerb_ReturnsTwoAndPrintsHelp()
    {
        var (_, stderr, exit) = await RunAsync( new[] { "frobnicate" } );
        exit.Should().Be( 2 );
        stderr.Should().Contain( "unknown verb" );
        stderr.Should().Contain( "frobnicate" );
    }

    [TestMethod]
    public async Task Main_SquashVerb_MissingArgs_NonZeroExitWithDiagnostic()
    {
        // The squash verb requires --provider, --connection, etc. Without
        // any args it returns non-zero and names the missing argument so
        // operators see what to fix.
        var (_, stderr, exit) = await RunAsync( new[] { "squash" } );
        exit.Should().NotBe( 0 );
        stderr.Should().Contain( "--provider" );
        stderr.Should().Contain( "required" );
    }

    [TestMethod]
    public async Task Main_RecoverVerb_MissingArgs_NonZeroExitWithDiagnostic()
    {
        var (_, stderr, exit) = await RunAsync( new[] { "recover" } );
        exit.Should().NotBe( 0 );
        // Surface message names the required subcommand (`from-mid-range`).
        stderr.Should().Contain( "from-mid-range" );
    }

    [TestMethod]
    public async Task Main_HelpText_MentionsAllFiveProviders()
    {
        // Stale CLI help text used to claim "v1 ships Postgres codegen;
        // other providers refuse." That was fixed for v3.0. Pin the
        // corrected line so a future copy-paste error doesn't restore the
        // stale prose.
        var (stdout, _, exit) = await RunAsync( new[] { "--help" } );
        exit.Should().Be( 0 );
        stdout.Should().Contain( "all 5 providers" );
        stdout.Should().Contain( "Postgres" );
        stdout.Should().Contain( "Aerospike" );
        stdout.Should().Contain( "OpenSearch" );
        stdout.Should().Contain( "MongoDB" );
        stdout.Should().Contain( "Couchbase" );
    }

    // ---- helpers --------------------------------------------------------

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync( string[] args )
    {
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();
        Console.SetOut( stdoutWriter );
        Console.SetError( stderrWriter );

        int exit;
        try
        {
            exit = await Program.Main( args );
        }
        finally
        {
            Console.SetOut( prevOut );
            Console.SetError( prevErr );
        }

        return (stdoutWriter.ToString(), stderrWriter.ToString(), exit);
    }
}
