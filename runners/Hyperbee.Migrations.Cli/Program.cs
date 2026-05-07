using Hyperbee.Migrations.Cli.Verbs;

namespace Hyperbee.Migrations.Cli;

internal static class Program
{
    public static async Task<int> Main( string[] args )
    {
        try
        {
            if ( args.Length == 0 || IsHelp( args[0] ) )
            {
                PrintHelp();
                return 0;
            }

            var verb = args[0].ToLowerInvariant();
            var verbArgs = args.Skip( 1 ).ToArray();

            return verb switch
            {
                "squash" => await SquashVerb.RunAsync( verbArgs ),
                "recover" => await RecoverVerb.RunAsync( verbArgs ),
                "version" => PrintVersion(),
                "--version" => PrintVersion(),
                _ => UnknownVerb( verb )
            };
        }
        catch ( Exception ex )
        {
            Console.Error.WriteLine( $"hyperbee-migrations: error: {ex.Message}" );
            return 1;
        }
    }

    private static bool IsHelp( string s ) =>
        string.Equals( s, "-h", StringComparison.OrdinalIgnoreCase )
        || string.Equals( s, "--help", StringComparison.OrdinalIgnoreCase )
        || string.Equals( s, "help", StringComparison.OrdinalIgnoreCase );

    private static int UnknownVerb( string verb )
    {
        Console.Error.WriteLine( $"hyperbee-migrations: unknown verb '{verb}'." );
        Console.Error.WriteLine();
        PrintHelp();
        return 2;
    }

    private static int PrintVersion()
    {
        var version = typeof( Program ).Assembly.GetName().Version?.ToString( 3 ) ?? "unknown";
        Console.WriteLine( $"hyperbee-migrations {version}" );
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine( "Hyperbee Migrations CLI" );
        Console.WriteLine();
        Console.WriteLine( "Usage:" );
        Console.WriteLine( "  hyperbee-migrations squash --provider <p> --connection <cs> --range <a>-<b> --output <dir> [--fleet-manifest <path>]" );
        Console.WriteLine( "  hyperbee-migrations recover from-mid-range --env <name> --token <token> --ticket-id <id> --reason <text>" );
        Console.WriteLine( "  hyperbee-migrations version" );
        Console.WriteLine();
        Console.WriteLine( "Verbs:" );
        Console.WriteLine( "  squash    Generate a destructive squash migration that subsumes a contiguous range." );
        Console.WriteLine( "            v1 ships Postgres codegen; other providers refuse with a roadmap-pointing message." );
        Console.WriteLine( "  recover   Last-resort recovery from a mid-range squash state. Requires a deterministic" );
        Console.WriteLine( "            acknowledgement token derived from (env-name, squash-version, missing-versions)." );
        Console.WriteLine();
        Console.WriteLine( "See: docs/decisions/0019-migration-squash-replaces-graph.md" );
    }
}
