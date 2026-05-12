using System.Reflection;
using System.Text.Json;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Cli.FleetManifest;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;

namespace Hyperbee.Migrations.Cli.Verbs;

/// <summary>
/// <c>hyperbee-migrations squash</c> -- generates a destructive squash
/// migration per ADR-0019. Thin dispatch shell over the
/// <see cref="ISquashCliProvider"/> contract (per ADR-0024): the CLI
/// assembly references no provider packages; per-provider implementations
/// are discovered via the migration assembly's reference closure.
/// </summary>
internal static class SquashVerb
{
    // R-12: per-verb flag whitelist.
    private static readonly ArgSchema Schema = ArgSchema.Of(
        knownFlags: new[]
        {
            "provider",
            "connection",
            "range",
            "output",
            "name",
            "assembly",
            "fleet-manifest",
            "no-fleet-manifest",
            "scan-source",
            "no-scan",
            "accept-stranding",
            "reason-stranding",
            "strand-ticket-id",
            "strand-owner",
            "remove-originals",
            "migrations-root",
            "regenerate",
            "confirm-delete",
            "provider-option",
        },
        booleanFlags: new[]
        {
            "remove-originals",
            "regenerate",
            "confirm-delete",
        } );

    public static async Task<int> RunAsync( string[] args )
    {
        ArgParser parsed;
        try
        {
            parsed = ArgParser.Parse( args, Schema );
        }
        catch ( ArgumentException ex )
        {
            return Fail( ex.Message );
        }

        string provider, connection, output, name, assemblyPath;
        string? fleetManifestPath;
        long fromVersion, toVersion;

        try
        {
            provider = parsed.Required( "provider" ).ToLowerInvariant();
            connection = parsed.Required( "connection" );
            (fromVersion, toVersion) = ArgParser.ParseRange( parsed.Required( "range" ) );
            output = parsed.Required( "output" );
            name = parsed.Optional( "name", $"Squash_{toVersion}" )!;
            assemblyPath = parsed.Required( "assembly" );
            fleetManifestPath = parsed.Optional( "fleet-manifest" );
        }
        catch ( ArgumentException ex )
        {
            return Fail( ex.Message );
        }

        // R-6 + R-7: validate the default-deny safety-gate flags BEFORE any
        // I/O. Both gates require explicit bypass with an audit reason.
        var scanSource = parsed.Optional( "scan-source" );
        var noScanReason = parsed.Optional( "no-scan" );
        if ( string.IsNullOrWhiteSpace( scanSource ) && string.IsNullOrWhiteSpace( noScanReason ) )
        {
            return Fail(
                "--scan-source <path-to-migrations-source> is required (ADR-0019 A5 default-deny). " +
                "To bypass deliberately, pass --no-scan=\"<reason>\" (>= 20 chars). " +
                "The scanner enforces [DataMigration] / [StructuralOnly] annotations; " +
                "skipping it allows data ops to silently disappear into a squash." );
        }
        if ( !string.IsNullOrWhiteSpace( scanSource ) && !string.IsNullOrWhiteSpace( noScanReason ) )
        {
            return Fail( "--scan-source and --no-scan are mutually exclusive. Pick one." );
        }
        if ( !string.IsNullOrWhiteSpace( noScanReason ) && noScanReason!.Trim().Length < 20 )
        {
            return Fail( "--no-scan requires a reason of at least 20 characters." );
        }

        var noFleetReason = parsed.Optional( "no-fleet-manifest" );
        if ( string.IsNullOrWhiteSpace( fleetManifestPath ) && string.IsNullOrWhiteSpace( noFleetReason ) )
        {
            return Fail(
                "--fleet-manifest <fleet.yml> is required (ADR-0019 A2 default-deny). " +
                "To bypass deliberately (e.g. solo-environment squash), pass " +
                "--no-fleet-manifest=\"<reason>\" (>= 20 chars). " +
                "Without the manifest the two-phase fleet readiness gate degrades " +
                "to a zero-phase no-op and mid-range fleet members are not detected." );
        }
        if ( !string.IsNullOrWhiteSpace( fleetManifestPath ) && !string.IsNullOrWhiteSpace( noFleetReason ) )
        {
            return Fail( "--fleet-manifest and --no-fleet-manifest are mutually exclusive. Pick one." );
        }
        if ( !string.IsNullOrWhiteSpace( noFleetReason ) && noFleetReason!.Trim().Length < 20 )
        {
            return Fail( "--no-fleet-manifest requires a reason of at least 20 characters." );
        }

        Directory.CreateDirectory( output );

        Console.WriteLine( $"[squash] provider={provider} range={fromVersion}-{toVersion} name={name}" );
        Console.WriteLine( $"[squash] loading assembly: {assemblyPath}" );

        // F-3: load via a collectible AssemblyLoadContext so the migration
        // assembly + its provider packages unload cleanly when the CLI's
        // verb completes. Necessary for embedding the CLI in long-running
        // hosts; a no-op for one-shot invocations but cheap.
        MigrationAssemblyLoader loader;
        try
        {
            loader = new MigrationAssemblyLoader( assemblyPath );
        }
        catch ( Exception ex )
        {
            return Fail( $"could not load --assembly '{assemblyPath}': {ex.Message}" );
        }
        using var _loader = loader;
        var migrationAssembly = loader.Assembly;

        // Discover ISquashCliProvider implementations from the migration
        // assembly's reference closure (per ADR-0024). NuGet package presence
        // IS the registration: the migration project references e.g.
        // Hyperbee.Migrations.Providers.Postgres.SquashCli, which contains
        // PostgresSquashCliProvider, and we find it transitively.
        IReadOnlyDictionary<string, ISquashCliProvider> providers;
        try
        {
            providers = CliProviderRegistry.Discover( migrationAssembly );
        }
        catch ( Exception ex )
        {
            return Fail( $"ISquashCliProvider discovery failed: {ex.Message}" );
        }

        if ( !providers.TryGetValue( provider, out var cliProvider ) )
        {
            var known = providers.Count == 0
                ? "<none>"
                : string.Join( ", ", providers.Keys.OrderBy( k => k, StringComparer.OrdinalIgnoreCase ) );
            Console.Error.WriteLine(
                $"hyperbee-migrations squash: provider `{provider}` has no ISquashCliProvider registered " +
                $"in the migration assembly's reference closure. Discovered providers: {known}. " +
                $"Add a package reference to Hyperbee.Migrations.Providers.{Capitalize( provider )}.SquashCli " +
                $"(or the equivalent for your provider) to the migration project." );
            return 4;
        }

        // Discover the IMigrationHost (per ADR-0024). The host wires the
        // operator's existing Add{Provider}Migrations setup; the CLI does
        // not need to know anything about the project's DI shape.
        IMigrationHost host;
        try
        {
            host = MigrationHostDiscovery.Discover( migrationAssembly );
        }
        catch ( Exception ex )
        {
            return Fail( $"IMigrationHost discovery failed: {ex.Message}" );
        }
        Console.WriteLine( $"[squash] migration host: {host.GetType().FullName}" );

        var descriptors = MigrationDescriptors.FromAssemblyInRange( migrationAssembly, fromVersion, toVersion );
        if ( descriptors.Count == 0 )
            return Fail( $"no [Migration] types found in {migrationAssembly.GetName().Name} for range {fromVersion}-{toVersion}." );

        Console.WriteLine( $"[squash] subsumed migrations ({descriptors.Count}): {string.Join( ", ", descriptors.Select( d => d.Attribute.Version ) )}" );

        if ( !string.IsNullOrWhiteSpace( noScanReason ) )
        {
            Console.WriteLine();
            Console.WriteLine(
                $"[squash] WARNING: --no-scan bypass in effect (reason: \"{noScanReason!.Trim()}\"). " +
                "ADR-0019 A5 source scanning is SKIPPED; any data ops in the subsumed migrations " +
                "will be elided into the squash. Operator owns the consequences." );
            Console.WriteLine();
        }
        if ( !string.IsNullOrWhiteSpace( scanSource ) )
        {
            // R-8: per-provider scanner dispatch via the ISquashCliProvider
            // contract. Postgres uses the existing Roslyn scanner; other
            // providers may have different heuristics (Aerospike's
            // call-site form, OpenSearch's REST-call patterns, etc.).
            Console.WriteLine( $"[squash] scanning migration source for [DataMigration] enforcement: {scanSource}" );
            IReadOnlyList<MigrationSourceVerdict> verdicts;
            try
            {
                verdicts = cliProvider.ScanSource( scanSource! );
            }
            catch ( Exception ex )
            {
                return Fail( $"source scan failed: {ex.Message}" );
            }

            var classNamesInRange = descriptors
                .Select( d => d.Type.Name )
                .ToHashSet( StringComparer.Ordinal );

            var offenders = verdicts
                .Where( v => v.RequiresAnnotation && classNamesInRange.Contains( v.ClassName ) )
                .ToArray();

            if ( offenders.Length > 0 )
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "[squash] FAILED ([DataMigration] enforcement per ADR-0019 A5): " +
                    $"{offenders.Length} class(es) carry data ops or non-determinism but lack " +
                    "[DataMigration] / [StructuralOnly] annotation:" );
                foreach ( var o in offenders )
                {
                    Console.Error.WriteLine( $"  - {o.ClassName} ({Path.GetFileName( o.FilePath )})" );
                    if ( o.DataOpHits.Count > 0 )
                        Console.Error.WriteLine( $"      data-op verbs: {string.Join( ", ", o.DataOpHits )}" );
                    if ( o.NonDeterminismHits.Count > 0 )
                        Console.Error.WriteLine( $"      non-determinism: {string.Join( ", ", o.NonDeterminismHits )}" );
                }
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "Annotate each offender with [DataMigration] (carried forward verbatim) or " +
                    "[StructuralOnly] (elided into the squash) and re-run." );
                return 8;
            }

            Console.WriteLine( $"[squash] source scan ok ({verdicts.Count} class(es) scanned)" );
        }

        // Build the per-provider options dictionary from --provider-option
        // key=value entries. Same shape feeds the fleet probe (RB-3) and the
        // provider's generation context.
        var providerOptions = ParseProviderOptions( parsed );

        if ( !string.IsNullOrWhiteSpace( noFleetReason ) )
        {
            Console.WriteLine();
            Console.WriteLine(
                $"[squash] WARNING: --no-fleet-manifest bypass in effect (reason: \"{noFleetReason!.Trim()}\"). " +
                "ADR-0019 A2 fleet-readiness gate is SKIPPED; mid-range fleet members will not be detected. " +
                "Operator owns the consequences." );
            Console.WriteLine();
        }

        FleetManifestModel? manifest = null;
        IReadOnlyDictionary<string, long> expectedFleetVersions = new Dictionary<string, long>();
        IReadOnlyList<SquashOverrideEntry> overrideEntries = Array.Empty<SquashOverrideEntry>();
        if ( !string.IsNullOrWhiteSpace( fleetManifestPath ) )
        {
            try
            {
                manifest = FleetManifestLoader.LoadFromFile( fleetManifestPath );
                Console.WriteLine( $"[squash] fleet manifest loaded ({manifest.Fleet.Count} envs, {manifest.SquashOverrides?.AcceptStranding.Count ?? 0} stranding overrides)" );
            }
            catch ( Exception ex )
            {
                return Fail( $"fleet manifest load failed: {ex.Message}" );
            }

            try
            {
                Console.WriteLine( "[squash] running fleet readiness check ..." );
                expectedFleetVersions = await FleetReadinessProbe.EnsureGenerableAsync(
                    cliProvider, manifest, fromVersion, toVersion, CancellationToken.None ).ConfigureAwait( false );
                foreach ( var (env, ver) in expectedFleetVersions.OrderBy( kv => kv.Key ) )
                    Console.WriteLine( $"  {env}: last-applied={ver}" );
            }
            catch ( MidRangeFleetException ex )
            {
                Console.Error.WriteLine( $"[squash] FAILED (fleet-readiness): {ex.Message}" );
                return 6;
            }
            catch ( Exception ex )
            {
                return Fail( $"fleet readiness probe failed: {ex.Message}" );
            }

            overrideEntries = FleetManifestLoader.BuildOverrideEntries( manifest, DateTimeOffset.UtcNow );
        }

        // CLI flags can supply stranding entries for ad-hoc squashes.
        IReadOnlyList<SquashOverrideEntry> flagOverrides;
        try
        {
            flagOverrides = BuildStrandingFromFlags( parsed );
        }
        catch ( ArgumentException ex )
        {
            return Fail( ex.Message );
        }

        if ( flagOverrides.Count > 0 )
        {
            var existing = overrideEntries.ToList();
            foreach ( var entry in flagOverrides )
            {
                if ( existing.Any( e => string.Equals( e.EnvironmentName, entry.EnvironmentName, StringComparison.OrdinalIgnoreCase ) ) )
                {
                    Console.WriteLine( $"[squash] WARNING: --accept-stranding `{entry.EnvironmentName}` already in fleet manifest; CLI flag overrides." );
                    existing.RemoveAll( e => string.Equals( e.EnvironmentName, entry.EnvironmentName, StringComparison.OrdinalIgnoreCase ) );
                }
                existing.Add( entry );
            }
            overrideEntries = existing;
            Console.WriteLine( $"[squash] {flagOverrides.Count} stranding entry(ies) supplied via CLI flags." );
        }

        var cliCtx = new SquashCliContext
        {
            SquashName = name,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            ConnectionString = connection,
            Descriptors = descriptors,
            MigrationHost = host,
            Options = new SquashGenerationOptions
            {
                LowerBound = fromVersion,
                UpperBound = toVersion
            },
            ProviderOptions = providerOptions
        };

        Console.WriteLine( "[squash] generating ..." );
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        SquashGenerationResult result;
        try
        {
            result = await cliProvider.GenerateAsync( cliCtx, CancellationToken.None ).ConfigureAwait( false );
        }
        catch ( Exception ex )
        {
            return Fail( $"GenerateAsync threw: {ex.Message}" );
        }
        stopwatch.Stop();

        switch ( result )
        {
            case SquashGenerationResult.Failed failed:
                Console.Error.WriteLine( $"[squash] FAILED: {failed.Detail}" );
                if ( failed.Cause != null )
                    Console.Error.WriteLine( failed.Cause );
                return 5;

            case SquashGenerationResult.Generated generated:
                await EmitArtifactsAsync(
                    output, name, cliProvider, generated, fromVersion, toVersion,
                    stopwatch.Elapsed, expectedFleetVersions, overrideEntries );

                // R-4: --remove-originals defaults to dry-run; actual deletion
                // requires --confirm-delete.
                if ( parsed.HasFlag( "remove-originals" ) )
                {
                    var migrationsRoot = parsed.Optional( "migrations-root" );
                    if ( string.IsNullOrWhiteSpace( migrationsRoot ) )
                    {
                        Console.Error.WriteLine(
                            "[squash] --remove-originals requires --migrations-root <path-to-migration-source-files>." );
                        return 7;
                    }

                    var rc = HandleRemoveOriginals(
                        migrationsRoot!,
                        descriptors.Select( d => d.Attribute.Version ).ToArray(),
                        confirmDelete: parsed.HasFlag( "confirm-delete" ),
                        regenerate: parsed.HasFlag( "regenerate" ) );
                    if ( rc != 0 )
                        return rc;
                }
                return 0;

            default:
                return Fail( $"unexpected result type {result.GetType().Name}" );
        }
    }

    private static async Task EmitArtifactsAsync(
        string output,
        string name,
        ISquashCliProvider provider,
        SquashGenerationResult.Generated gen,
        long fromVersion,
        long toVersion,
        TimeSpan elapsed,
        IReadOnlyDictionary<string, long> expectedFleetVersions,
        IReadOnlyList<SquashOverrideEntry> overrideEntries )
    {
        // R-5: file extension is provider-declared. Postgres -> .sql,
        // the four NoSQL providers -> .statements (per ADR-0022).
        var contentPath = Path.Combine( output, name + provider.SquashFileExtension );
        var metadataPath = Path.Combine( output, $"{name}.metadata.json" );
        var summaryPath = Path.Combine( output, $"{name}.summary.md" );

        await File.WriteAllTextAsync( contentPath, gen.Content );

        var toolVersion = typeof( SquashVerb ).Assembly.GetName().Version?.ToString( 3 ) ?? "1.0.0";

        var metadata = new SquashMetadata
        {
            ReplacesFromVersion = fromVersion,
            ReplacesToVersion = toVersion,
            ProviderId = provider.ProviderId,
            Topology = gen.Topology.Properties,
            CanonicalizerVersion = $"{provider.ProviderId}/1.0.0",
            ExpectedFleetVersions = expectedFleetVersions.ToDictionary( kv => kv.Key, kv => kv.Value ),
            SquashOverrides = overrideEntries.ToArray(),
            CodegenToolVersion = $"hyperbee-migrations/{toolVersion}",
            GeneratedAt = DateTimeOffset.UtcNow
        };

        await File.WriteAllTextAsync( metadataPath, JsonSerializer.Serialize( metadata, new JsonSerializerOptions { WriteIndented = true } ) );

        var topologyLine = string.Join( ", ", gen.Topology.Properties.OrderBy( kv => kv.Key ).Select( kv => $"{kv.Key}={kv.Value}" ) );
        var summary =
            $"# Squash {fromVersion}..{toVersion}\n\n" +
            $"- Provider: {provider.ProviderId}\n" +
            $"- Generated: {metadata.GeneratedAt:O}\n" +
            $"- Tool: {metadata.CodegenToolVersion}\n" +
            $"- Topology: {topologyLine}\n" +
            $"- Replaces ({gen.Replaces.Count} versions): {string.Join( ", ", gen.Replaces )}\n" +
            $"- Elapsed: {elapsed.TotalSeconds:F2}s\n\n" +
            $"## Diagnostics\n\n" +
            (gen.Diagnostics.Count == 0 ? "_(none)_\n" : string.Join( "\n", gen.Diagnostics.Select( d => $"- {d}" ) ) + "\n");
        await File.WriteAllTextAsync( summaryPath, summary );

        Console.WriteLine();
        Console.WriteLine( $"[squash] OK: emitted {gen.Replaces.Count} replaced versions; elapsed {elapsed.TotalSeconds:F2}s" );
        Console.WriteLine( $"  {contentPath}" );
        Console.WriteLine( $"  {metadataPath}" );
        Console.WriteLine( $"  {summaryPath}" );
        if ( gen.Diagnostics.Count > 0 )
        {
            Console.WriteLine();
            Console.WriteLine( $"[squash] {gen.Diagnostics.Count} diagnostic(s) -- review {summaryPath} before applying." );
        }
    }

    private static int Fail( string message )
    {
        Console.Error.WriteLine( $"hyperbee-migrations squash: {message}" );
        Console.Error.WriteLine();
        PrintHelp();
        return 2;
    }

    private static int HandleRemoveOriginals( string migrationsRoot, long[] versions, bool confirmDelete, bool regenerate )
    {
        // R-4: default behavior is now dry-run. Matched files are listed but
        // not deleted unless --confirm-delete is also supplied. The version-
        // delimited regex prevents false-positive matches against names that
        // contain the version as a substring (e.g. squashing 1000 must not
        // match `User_1000_Backup.cs` -- the digits 1000 must be flanked by
        // non-digit boundaries).
        if ( !Directory.Exists( migrationsRoot ) )
        {
            Console.Error.WriteLine( $"[squash] --migrations-root `{migrationsRoot}` does not exist." );
            return 7;
        }

        var foundByVersion = new Dictionary<long, List<string>>();
        foreach ( var version in versions )
        {
            var versionStr = version.ToString( System.Globalization.CultureInfo.InvariantCulture );
            var matches = Directory.GetFiles( migrationsRoot, "*.cs", SearchOption.AllDirectories )
                .Where( f =>
                {
                    var fileName = Path.GetFileNameWithoutExtension( f );
                    return System.Text.RegularExpressions.Regex.IsMatch(
                        fileName,
                        @"(^|[^0-9])" + System.Text.RegularExpressions.Regex.Escape( versionStr ) + @"([^0-9]|$)" );
                } )
                .ToList();
            if ( matches.Count > 0 )
                foundByVersion[version] = matches;
        }

        if ( foundByVersion.Count == 0 )
        {
            if ( regenerate )
            {
                Console.WriteLine(
                    "[squash] --remove-originals: no source files found for any subsumed version. " +
                    "--regenerate set; treating as success (artifacts re-emitted)." );
                return 0;
            }
            Console.Error.WriteLine(
                "[squash] --remove-originals: no source files found for any subsumed version. " +
                "Either the originals are already removed (pass --regenerate to confirm) or " +
                "--migrations-root points at the wrong directory." );
            return 7;
        }

        Console.WriteLine();
        if ( !confirmDelete )
        {
            // Dry-run: list matched files and exit without modifying disk.
            Console.WriteLine(
                "[squash] --remove-originals (DRY-RUN; pass --confirm-delete to actually remove):" );
            foreach ( var (version, files) in foundByVersion.OrderBy( kv => kv.Key ) )
            {
                foreach ( var file in files )
                {
                    Console.WriteLine( $"  v{version}: {Path.GetRelativePath( migrationsRoot, file )}" );
                }
            }
            Console.WriteLine();
            Console.WriteLine(
                "[squash] DRY-RUN: review the file list above, then re-run with --confirm-delete to remove." );
            return 0;
        }

        Console.WriteLine( "[squash] --remove-originals removing:" );
        foreach ( var (version, files) in foundByVersion.OrderBy( kv => kv.Key ) )
        {
            foreach ( var file in files )
            {
                Console.WriteLine( $"  v{version}: {Path.GetRelativePath( migrationsRoot, file )}" );
                File.Delete( file );
            }
        }

        var notFound = versions.Except( foundByVersion.Keys ).ToArray();
        if ( notFound.Length > 0 )
        {
            Console.WriteLine();
            Console.WriteLine(
                $"[squash] --remove-originals: {notFound.Length} version(s) had no source file " +
                $"to remove (already gone or never had one): [{string.Join( ", ", notFound )}]." );
        }

        return 0;
    }

    private static IReadOnlyList<SquashOverrideEntry> BuildStrandingFromFlags( ArgParser parsed )
    {
        var names = parsed.Many( "accept-stranding" )
            .SelectMany( v => v.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
            .Where( n => !string.IsNullOrWhiteSpace( n ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToArray();

        if ( names.Length == 0 )
            return Array.Empty<SquashOverrideEntry>();

        var reasons = parsed.Many( "reason-stranding" )
            .Select( raw =>
            {
                var eq = raw.IndexOf( '=' );
                if ( eq <= 0 )
                    throw new ArgumentException( $"--reason-stranding `{raw}`: expected format name=reason." );
                var n = raw.Substring( 0, eq ).Trim();
                var r = raw.Substring( eq + 1 ).Trim();
                if ( r.Length < 20 )
                    throw new ArgumentException(
                        $"--reason-stranding for `{n}`: reason must be >= 20 characters." );
                return (Name: n, Reason: r);
            } )
            .ToDictionary( x => x.Name, x => x.Reason, StringComparer.OrdinalIgnoreCase );

        var missing = names.Where( n => !reasons.ContainsKey( n ) ).ToArray();
        if ( missing.Length > 0 )
            throw new ArgumentException(
                $"--accept-stranding requires a paired --reason-stranding for each environment. " +
                $"Missing reasons for: [{string.Join( ", ", missing )}]." );

        var ticketId = parsed.Optional( "strand-ticket-id", "AD-HOC-CLI" )!;
        var owner = parsed.Optional( "strand-owner" )
            ?? Environment.GetEnvironmentVariable( "USER" )
            ?? Environment.GetEnvironmentVariable( "USERNAME" )
            ?? "cli-operator";

        var expires = DateTimeOffset.UtcNow.AddDays( 30 );

        return names
            .Select( n => new SquashOverrideEntry
            {
                EnvironmentName = n,
                TicketId = ticketId,
                Owner = owner,
                Reason = reasons[n],
                Expires = expires
            } )
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> ParseProviderOptions( ArgParser parsed )
    {
        var result = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var raw in parsed.Many( "provider-option" ) )
        {
            var eq = raw.IndexOf( '=' );
            if ( eq <= 0 )
                throw new ArgumentException( $"--provider-option `{raw}`: expected format key=value." );
            var key = raw.Substring( 0, eq ).Trim();
            var value = raw.Substring( eq + 1 ).Trim();
            if ( string.IsNullOrEmpty( key ) )
                throw new ArgumentException( $"--provider-option `{raw}`: empty key." );
            result[key] = value;
        }
        return result;
    }

    private static string Capitalize( string s )
        => string.IsNullOrEmpty( s ) ? s : char.ToUpperInvariant( s[0] ) + s.Substring( 1 );

    private static void PrintHelp()
    {
        Console.WriteLine(
            "Usage: hyperbee-migrations squash \\\n" +
            "         --provider <postgres|aerospike|mongodb|couchbase|opensearch> \\\n" +
            "         --connection \"<provider-specific-connection-string>\" \\\n" +
            "         --range <fromVersion>-<toVersion> \\\n" +
            "         --output <directory> \\\n" +
            "         --assembly <path-to-MyApp.Migrations.dll> \\\n" +
            "         (--scan-source <path-to-migrations-source> | --no-scan=\"<reason>\") \\\n" +
            "         (--fleet-manifest <fleet.yml>             | --no-fleet-manifest=\"<reason>\") \\\n" +
            "         [--name Squash_<toVersion>] \\\n" +
            "         [--provider-option key=value [--provider-option key=value ...]] \\\n" +
            "         [--accept-stranding name1,name2 --reason-stranding name1=\"...\" --reason-stranding name2=\"...\"] \\\n" +
            "         [--strand-ticket-id FLEET-1234 --strand-owner ops@example.com] \\\n" +
            "         [--remove-originals [--confirm-delete] [--regenerate]]\n" +
            "\n" +
            "  The CLI dispatches to the ISquashCliProvider registered in the\n" +
            "  migration assembly's reference closure. Add the relevant SquashCli\n" +
            "  package to the migration project (e.g.\n" +
            "  `Hyperbee.Migrations.Providers.Postgres.SquashCli`) to enable\n" +
            "  per-provider codegen. v3.0 ships all 5 providers; no provider is\n" +
            "  hardcoded in the CLI.\n" +
            "\n" +
            "  --remove-originals defaults to dry-run; pass --confirm-delete to\n" +
            "  actually delete matched files (R-4)." );
    }
}

/// <summary>
/// Loads <see cref="MigrationDescriptor"/>s directly from an assembly's
/// reflected <see cref="MigrationAttribute"/> annotations, filtered by an
/// inclusive version range.
/// </summary>
internal static class MigrationDescriptors
{
    public static IReadOnlyList<MigrationDescriptor> FromAssemblyInRange(
        Assembly assembly,
        long fromVersion,
        long toVersion )
    {
        var descriptors = new List<MigrationDescriptor>();
        foreach ( var type in assembly.GetTypes() )
        {
            if ( !typeof( Migration ).IsAssignableFrom( type ) || type.IsAbstract )
                continue;

            var attr = type.GetCustomAttribute<MigrationAttribute>();
            if ( attr == null )
                continue;

            if ( attr.Version < fromVersion || attr.Version > toVersion )
                continue;

            descriptors.Add( new MigrationDescriptor( type, attr, Array.Empty<long>() ) );
        }

        return descriptors
            .OrderBy( d => d.Attribute.Version )
            .ToArray();
    }
}
