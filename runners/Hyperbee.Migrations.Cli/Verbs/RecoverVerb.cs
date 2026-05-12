using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Postgres;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hyperbee.Migrations.Cli.Verbs;

/// <summary>
/// <c>hyperbee-migrations recover from-mid-range</c> — last-resort recovery
/// from a mid-range squash state per ADR-0019 A3. Verifies the deterministic
/// acknowledgement token and (in v1) prints the audit-ready record. Actual
/// ledger mutation hooks ship with the runner integration.
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
            // RB-2: connection details for persisting the recovery acknowledgement.
            // Provider-coupled today; Week 2 IMigrationHost discovery generalizes
            // across all 5 providers.
            "provider",
            "connection",
            "schema-name",
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
        string env, ticketId, reason, suppliedToken;
        long squashVersion;
        long[] missingVersions;

        string provider, connection;
        string schemaName;
        try
        {
            env = parsed.Required( "env" );
            ticketId = parsed.Required( "ticket-id" );
            reason = parsed.Required( "reason" );
            suppliedToken = parsed.Required( "token" );
            provider = parsed.Required( "provider" ).ToLowerInvariant();
            connection = parsed.Required( "connection" );
            schemaName = parsed.Optional( "schema-name", "migration" )!;

            if ( !long.TryParse( parsed.Required( "squash-version" ), out squashVersion ) )
                throw new ArgumentException( "--squash-version must be an integer." );

            var missingRaw = parsed.Required( "missing-versions" );
            missingVersions = ParseMissingVersions( missingRaw );

            if ( reason.Trim().Length < 20 )
                throw new ArgumentException( "--reason must be at least 20 characters." );

            // Loose ticket-id validation: alphanumeric + dashes/underscores,
            // 3-64 chars. Matches common ticket schemas (JIRA-1234, INC-001).
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

        // RB-2: persist the acknowledgement row so the runner can read it on
        // the next invocation and force-mark the squash without running its
        // body. v3.0 CLI persistence is provider-coupled (Postgres only
        // today); Week 2 IMigrationHost discovery generalizes this across
        // all 5 providers via the migration project's host class.
        if ( provider != "postgres" )
        {
            Console.Error.WriteLine(
                $"hyperbee-migrations recover: v3.0 CLI persists recovery acknowledgements via " +
                $"Postgres only; --provider `{provider}` will be supported once IMigrationHost " +
                $"discovery lands (Week 2 of the v3.0 release cascade)." );
            return 4;
        }

        var recoveryRow = RecoveryRecord.Build( squashVersion, env, missingVersions );

        try
        {
            await PersistPostgresRecoveryAsync( connection, schemaName, recoveryRow );
        }
        catch ( Exception ex )
        {
            Console.Error.WriteLine(
                $"hyperbee-migrations recover: persistence failed: {ex.Message}" );
            return 5;
        }

        // Audit-ready summary alongside the persistence confirmation.
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

    private static async Task PersistPostgresRecoveryAsync( string connection, string schemaName, MigrationRecord row )
    {
        // Build a minimal DI graph mirroring what AddPostgresMigrations
        // would wire at runtime, scoped just to what PostgresRecordStore
        // needs. The migration assembly is irrelevant to recovery row
        // persistence (we're writing to the ledger, not scanning for
        // migrations).
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging( b => b.AddProvider( NullLoggerProvider.Instance ) );

        var dataSourceBuilder = new NpgsqlDataSourceBuilder( connection );
        await using var dataSource = dataSourceBuilder.Build();
        services.AddSingleton( dataSource );
        services.AddPostgresMigrations( opts =>
        {
            opts.SchemaName = schemaName;
            opts.LockingEnabled = false; // recovery write does not race with itself
        } );

        await using var sp = services.BuildServiceProvider();
        // PostgresRecordStore is internal; resolve via the public
        // IMigrationRecordStore alias that AddPostgresMigrations installs.
        var store = sp.GetRequiredService<IMigrationRecordStore>();

        // InitializeAsync is idempotent + IF NOT EXISTS-shaped; safe even when
        // the ledger already exists. Without it the first recover-on-fresh-db
        // would fail with "table does not exist".
        await store.InitializeAsync( CancellationToken.None );

        var outcome = await store.WriteAsync( row, WritePrecondition.MustNotExist );
        if ( outcome == WriteOutcome.AlreadyExistsBenign )
        {
            // Operator already wrote this acknowledgement (identical row).
            // Idempotent. Report and return.
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
            "         --provider postgres \\\n" +
            "         --connection \"Host=...;Database=...;Username=...;Password=...\" \\\n" +
            "         --env <env-name> \\\n" +
            "         --squash-version <version> \\\n" +
            "         --missing-versions <v1,v2,...> \\\n" +
            "         --token <12-hex-acknowledgement> \\\n" +
            "         --ticket-id <FLEET-1234> \\\n" +
            "         --reason \"... at least 20 characters ...\" \\\n" +
            "         [--schema-name migration]\n" +
            "\n" +
            "  Persists a recovery acknowledgement row into the migration ledger so the\n" +
            "  next runner invocation force-marks the mid-range squash without running\n" +
            "  its body. Per ADR-0019 A3 / RB-2: last resort, DBA-supervised, post-\n" +
            "  incident only -- the live data state MUST already match the squashed\n" +
            "  schema." );
    }
}
