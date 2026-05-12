using System.Reflection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Cli.Verbs;

/// <summary>
/// <c>hyperbee-migrations recover from-mid-range</c> -- persists a recovery
/// acknowledgement row into the migration ledger so the runner force-marks
/// the mid-range squash on next invocation (per ADR-0019 A3 + RB-2).
/// Routes through the discovered <see cref="IMigrationHost"/> so the CLI
/// references no provider packages -- the migration project's existing
/// <c>Add{Provider}Migrations</c> wiring is the single source of truth
/// for which provider to write against.
/// </summary>
internal static class RecoverVerb
{
    // R-12: per-verb flag whitelist.
    private static readonly ArgSchema Schema = ArgSchema.Of(
        knownFlags: new[]
        {
            "env",
            "squash-version",
            "missing-versions",
            "token",
            "ticket-id",
            "reason",
            "connection",
            "assembly",
        },
        booleanFlags: Array.Empty<string>() );

    public static async Task<int> RunAsync( string[] args )
    {
        if ( args.Length == 0 || !string.Equals( args[0], "from-mid-range", StringComparison.OrdinalIgnoreCase ) )
        {
            Console.Error.WriteLine( "hyperbee-migrations recover: only 'from-mid-range' is supported in v1." );
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }

        ArgParser parsed;
        try
        {
            parsed = ArgParser.Parse( args.Skip( 1 ).ToArray(), Schema );
        }
        catch ( ArgumentException ex )
        {
            Console.Error.WriteLine( $"hyperbee-migrations recover: {ex.Message}" );
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }

        string env, ticketId, reason, suppliedToken, connection, assemblyPath;
        long squashVersion;
        long[] missingVersions;

        try
        {
            env = parsed.Required( "env" );
            ticketId = parsed.Required( "ticket-id" );
            reason = parsed.Required( "reason" );
            suppliedToken = parsed.Required( "token" );
            connection = parsed.Required( "connection" );
            assemblyPath = parsed.Required( "assembly" );

            if ( !long.TryParse( parsed.Required( "squash-version" ), out squashVersion ) )
                throw new ArgumentException( "--squash-version must be an integer." );

            var missingRaw = parsed.Required( "missing-versions" );
            missingVersions = ParseMissingVersions( missingRaw );

            if ( reason.Trim().Length < 20 )
                throw new ArgumentException( "--reason must be at least 20 characters." );

            if ( !System.Text.RegularExpressions.Regex.IsMatch( ticketId, @"^[A-Za-z0-9_\-]{3,64}$" ) )
                throw new ArgumentException( "--ticket-id must be 3-64 alphanumeric / dash / underscore characters." );
        }
        catch ( ArgumentException ex )
        {
            Console.Error.WriteLine( $"hyperbee-migrations recover: {ex.Message}" );
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }

        if ( !RecoveryAcknowledgement.Verify( env, squashVersion, missingVersions, suppliedToken ) )
        {
            var expected = RecoveryAcknowledgement.ComputeToken( env, squashVersion, missingVersions );
            Console.Error.WriteLine(
                $"hyperbee-migrations recover: token does NOT match the expected acknowledgement for " +
                $"(env={env}, squash={squashVersion}, missing=[{string.Join( ",", missingVersions )}]). " +
                $"Recompute and retry. Expected: {expected}" );
            return 3;
        }

        // Load the migration assembly and discover its IMigrationHost
        // (per ADR-0024). The host provides the configured IServiceProvider
        // -- whichever provider the migration project uses, we resolve its
        // IMigrationRecordStore and write the recovery row.
        Assembly migrationAssembly;
        try
        {
            migrationAssembly = Assembly.LoadFrom( Path.GetFullPath( assemblyPath ) );
        }
        catch ( Exception ex )
        {
            Console.Error.WriteLine( $"hyperbee-migrations recover: could not load --assembly '{assemblyPath}': {ex.Message}" );
            return 2;
        }

        IMigrationHost host;
        try
        {
            host = MigrationHostDiscovery.Discover( migrationAssembly );
        }
        catch ( Exception ex )
        {
            Console.Error.WriteLine( $"hyperbee-migrations recover: IMigrationHost discovery failed: {ex.Message}" );
            return 2;
        }

        var recoveryRow = RecoveryRecord.Build( squashVersion, env, missingVersions );

        try
        {
            await PersistViaHostAsync( host, connection, recoveryRow );
        }
        catch ( Exception ex )
        {
            Console.Error.WriteLine( $"hyperbee-migrations recover: persistence failed: {ex.Message}" );
            return 5;
        }

        Console.WriteLine( "[recover from-mid-range] acknowledgement valid and PERSISTED." );
        Console.WriteLine( $"  env             : {env}" );
        Console.WriteLine( $"  squash-version  : {squashVersion}" );
        Console.WriteLine( $"  missing-versions: [{string.Join( ", ", missingVersions )}]" );
        Console.WriteLine( $"  ticket-id       : {ticketId}" );
        Console.WriteLine( $"  reason          : {reason}" );
        Console.WriteLine( $"  token           : {suppliedToken.Trim().ToLowerInvariant()}" );
        Console.WriteLine( $"  recovery-row-id : {recoveryRow.Id}" );
        Console.WriteLine( $"  acknowledged-at : {recoveryRow.RunOn:O}" );
        Console.WriteLine();
        Console.WriteLine(
            "Next step: run the standard `dotnet hyperbee-migrations` runner against this environment. " +
            "It will read the recovery row, re-verify the token, force-mark the squash without running " +
            "its body, then delete the recovery row. Per ADR-0019 A3 this path is documented as a last " +
            "resort, DBA-supervised, post-incident only." );

        return 0;
    }

    private static async Task PersistViaHostAsync( IMigrationHost host, string connection, MigrationRecord row )
    {
        var ctx = new MigrationHostContext( connection )
        {
            OverrideOptions = opts =>
            {
                // RB-2 persist itself doesn't race with the runner (operator
                // runs recover as a manual one-off); disable locking to
                // skip the bootstrap-acquire overhead.
                opts.LockingEnabled = false;
            }
        };

        var serviceProvider = await host.ConfigureAsync( ctx, CancellationToken.None ).ConfigureAwait( false );
        try
        {
            var store = serviceProvider.GetRequiredService<IMigrationRecordStore>();
            await store.InitializeAsync( CancellationToken.None ).ConfigureAwait( false );

            var outcome = await store.WriteAsync( row, WritePrecondition.MustNotExist ).ConfigureAwait( false );
            if ( outcome == WriteOutcome.AlreadyExistsBenign )
            {
                Console.WriteLine(
                    "[recover from-mid-range] NOTE: a recovery row with this id already exists; " +
                    "row contents are identical -- treating as idempotent success." );
            }
            else if ( outcome == WriteOutcome.PreconditionFailed )
            {
                throw new InvalidOperationException(
                    "recovery row id collision: a different acknowledgement already exists at " +
                    $"id `{row.Id}`. Delete the stale row manually and retry, or rerun recover " +
                    "for the (env, squash) pair that the stale row actually targets." );
            }
        }
        finally
        {
            if ( serviceProvider is IAsyncDisposable iad ) await iad.DisposeAsync().ConfigureAwait( false );
            else if ( serviceProvider is IDisposable d ) d.Dispose();
        }
    }

    private static long[] ParseMissingVersions( string raw )
    {
        var parts = raw.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if ( parts.Length == 0 )
            throw new ArgumentException( "--missing-versions must list at least one version (comma-separated integers)." );

        var versions = new long[parts.Length];
        for ( var i = 0; i < parts.Length; i++ )
        {
            if ( !long.TryParse( parts[i], out versions[i] ) )
                throw new ArgumentException( $"--missing-versions: '{parts[i]}' is not an integer." );
        }
        return versions;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            "Usage: hyperbee-migrations recover from-mid-range \\\n" +
            "         --connection \"<provider-specific-connection-string>\" \\\n" +
            "         --assembly <path-to-MyApp.Migrations.dll> \\\n" +
            "         --env <env-name> \\\n" +
            "         --squash-version <version> \\\n" +
            "         --missing-versions <v1,v2,...> \\\n" +
            "         --token <12-hex-acknowledgement> \\\n" +
            "         --ticket-id <FLEET-1234> \\\n" +
            "         --reason \"... at least 20 characters ...\"\n" +
            "\n" +
            "  Persists a recovery acknowledgement row into the migration ledger so the\n" +
            "  next runner invocation force-marks the mid-range squash without running\n" +
            "  its body. Routes through the IMigrationHost discovered in --assembly's\n" +
            "  reference closure (per ADR-0024). Per ADR-0019 A3 / RB-2: last resort,\n" +
            "  DBA-supervised, post-incident only -- the live data state MUST already\n" +
            "  match the squashed schema." );
    }
}
