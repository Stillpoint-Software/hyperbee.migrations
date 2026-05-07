using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Cli.Verbs;

/// <summary>
/// <c>hyperbee-migrations recover from-mid-range</c> — last-resort recovery
/// from a mid-range squash state per ADR-0019 A3. Verifies the deterministic
/// acknowledgement token and (in v1) prints the audit-ready record. Actual
/// ledger mutation hooks ship with the runner integration.
/// </summary>
internal static class RecoverVerb
{
    public static Task<int> RunAsync( string[] args )
    {
        if ( args.Length == 0 || !string.Equals( args[0], "from-mid-range", StringComparison.OrdinalIgnoreCase ) )
        {
            Console.Error.WriteLine( "hyperbee-migrations recover: only 'from-mid-range' is supported in v1." );
            Console.Error.WriteLine();
            PrintHelp();
            return Task.FromResult( 2 );
        }

        var parsed = ArgParser.Parse( args.Skip( 1 ).ToArray() );
        string env, ticketId, reason, suppliedToken;
        long squashVersion;
        long[] missingVersions;

        try
        {
            env = parsed.Required( "env" );
            ticketId = parsed.Required( "ticket-id" );
            reason = parsed.Required( "reason" );
            suppliedToken = parsed.Required( "token" );

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
            return Task.FromResult( 2 );
        }

        if ( !RecoveryAcknowledgement.Verify( env, squashVersion, missingVersions, suppliedToken ) )
        {
            var expected = RecoveryAcknowledgement.ComputeToken( env, squashVersion, missingVersions );
            Console.Error.WriteLine(
                $"hyperbee-migrations recover: token does NOT match the expected acknowledgement for " +
                $"(env={env}, squash={squashVersion}, missing=[{string.Join( ",", missingVersions )}]). " +
                $"Recompute and retry. Expected: {expected}" );
            return Task.FromResult( 3 );
        }

        // Audit-ready summary. The actual ledger mutation hook (writing the
        // squash row with Kind=Squash + appropriate Replaces[]) is performed
        // by the runner against the operator's live store; this CLI verb's
        // job in v1 is to validate the acknowledgement and emit the audit
        // trail.
        Console.WriteLine( "[recover from-mid-range] acknowledgement valid." );
        Console.WriteLine( $"  env             : {env}" );
        Console.WriteLine( $"  squash-version  : {squashVersion}" );
        Console.WriteLine( $"  missing-versions: [{string.Join( ", ", missingVersions )}]" );
        Console.WriteLine( $"  ticket-id       : {ticketId}" );
        Console.WriteLine( $"  reason          : {reason}" );
        Console.WriteLine( $"  token           : {suppliedToken.Trim().ToLowerInvariant()}" );
        Console.WriteLine( $"  acknowledged-at : {DateTimeOffset.UtcNow:O}" );
        Console.WriteLine();
        Console.WriteLine(
            "Next step: run the standard `dotnet hyperbee-migrations` runner against this environment. " +
            "It will see the recovery audit and force-mark the squash without running its body. " +
            "Per ADR-0019 A3 this path is documented as a last resort, DBA-supervised, post-incident only." );

        return Task.FromResult( 0 );
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
            "         --env <env-name> \\\n" +
            "         --squash-version <version> \\\n" +
            "         --missing-versions <v1,v2,...> \\\n" +
            "         --token <12-hex-acknowledgement> \\\n" +
            "         --ticket-id <FLEET-1234> \\\n" +
            "         --reason \"... at least 20 characters ...\"" );
    }
}
