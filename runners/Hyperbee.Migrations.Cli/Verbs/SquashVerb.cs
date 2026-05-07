using System.Reflection;
using System.Text.Json;
using Hyperbee.Migrations.Cli.FleetManifest;
using Hyperbee.Migrations.Cli.Postgres;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Hyperbee.Migrations.Squash;
using Npgsql;

namespace Hyperbee.Migrations.Cli.Verbs;

/// <summary>
/// <c>hyperbee-migrations squash</c> — generates a destructive squash
/// migration per ADR-0019. v1 only ships Postgres codegen; other providers
/// surface a roadmap-pointing refusal via <see cref="NullSquashStrategy"/>.
/// </summary>
internal static class SquashVerb
{
    public static async Task<int> RunAsync( string[] args )
    {
        ArgParser parsed;
        try
        {
            parsed = ArgParser.Parse( args );
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

        if ( provider != "postgres" )
        {
            // Per A11: NullSquashStrategy returns Failed naming the roadmap phase.
            var roadmap = provider switch
            {
                "mongodb" or "couchbase" => "v1.1",
                "aerospike" or "opensearch" => "v1.2",
                _ => "a future release"
            };
            Console.Error.WriteLine(
                $"hyperbee-migrations squash: codegen for `{provider}` ships in {roadmap}; see release roadmap. " +
                "Current options: continue applying migrations individually." );
            return 4;
        }

        Directory.CreateDirectory( output );

        Console.WriteLine( $"[squash] provider={provider} range={fromVersion}-{toVersion} name={name}" );
        Console.WriteLine( $"[squash] loading assembly: {assemblyPath}" );

        Assembly migrationAssembly;
        try
        {
            migrationAssembly = Assembly.LoadFrom( Path.GetFullPath( assemblyPath ) );
        }
        catch ( Exception ex )
        {
            return Fail( $"could not load --assembly '{assemblyPath}': {ex.Message}" );
        }

        // Build descriptors from the assembly's [Migration] types in the
        // requested range. We don't need ResolvedReplaces here — Phase 6
        // strategy uses the raw range to bound capture.
        var descriptors = MigrationDescriptors.FromAssemblyInRange( migrationAssembly, fromVersion, toVersion );
        if ( descriptors.Count == 0 )
            return Fail( $"no [Migration] types found in {migrationAssembly.GetName().Name} for range {fromVersion}-{toVersion}." );

        Console.WriteLine( $"[squash] subsumed migrations ({descriptors.Count}): {string.Join( ", ", descriptors.Select( d => d.Attribute.Version ) )}" );

        // Optional: Roslyn-based migration source scanner enforces the
        // [DataMigration] / [StructuralOnly] annotation requirement per
        // ADR-0019 amendment A5. Operator supplies --scan-source <dir>
        // pointing at the migrations source folder; the scanner refuses
        // generation if any subsumed class looks like a data op AND lacks
        // both annotations.
        var scanSource = parsed.Optional( "scan-source" );
        if ( !string.IsNullOrWhiteSpace( scanSource ) )
        {
            Console.WriteLine( $"[squash] scanning migration source for [DataMigration] enforcement: {scanSource}" );
            var verdicts = PostgresMigrationSourceScanner.Scan( scanSource );

            // Restrict to the version range we're squashing (best-effort —
            // the scanner doesn't know versions, so we cross-reference by
            // class name against the descriptors).
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

        // Optional fleet manifest. When supplied, drive the pre-generation
        // readiness check: refuses if any registered fleet member is mid-range
        // (per ADR-0019 A2). The probed last-applied versions feed into
        // SquashMetadata.ExpectedFleetVersions for the deploy-time gate.
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
                expectedFleetVersions = await FleetReadinessCheck.EnsureGenerableAsync(
                    manifest, fromVersion, toVersion, CancellationToken.None ).ConfigureAwait( false );
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

        // CLI flags can ALSO supply stranding entries for ad-hoc squashes
        // without a manifest (or to extend the manifest's set). Per A11:
        // each name supplied via --accept-stranding requires a paired
        // --reason-stranding name="..." (>= 20 chars) entry. ticket-id +
        // owner are taken from --strand-ticket-id and --strand-owner
        // (defaults to git config user.email when omitted).
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

        await using var dataSource = NpgsqlDataSource.Create( connection );

        // Concrete snapshot capture: ephemeral postgres:N-alpine container
        // (per A10 server-version-matched). Caller-supplied applyMigrations
        // delegate is a TODO for v1.0 — the operator's project shape varies.
        // For v1 the CLI applies the captured migrations via dotnet ef-style
        // reflection IF the assembly exposes a static "ApplyAsync" entry,
        // otherwise the operator must wire their own capture. We surface this
        // as a clear error message naming the path forward.
        var capture = new PostgresEphemeralCapture( async ( ds, upTo, ct ) =>
        {
            var applyEntry = migrationAssembly
                .GetTypes()
                .Select( t => t.GetMethod( "ApplyToDataSourceAsync",
                    BindingFlags.Public | BindingFlags.Static,
                    new[] { typeof( NpgsqlDataSource ), typeof( long ), typeof( CancellationToken ) } ) )
                .FirstOrDefault( m => m != null );

            if ( applyEntry == null )
            {
                throw new NotSupportedException(
                    "v1 squash CLI requires the migration assembly to expose a static method " +
                    "`Task ApplyToDataSourceAsync(NpgsqlDataSource ds, long upTo, CancellationToken ct)` " +
                    "that applies the discovered migrations through `upTo`. This is a v1 transitional " +
                    "shim; v1.0.x will replace it with a generic MigrationRunner-driven path." );
            }

            await (Task) applyEntry.Invoke( null, new object[] { ds, upTo, ct } )!;
        } );

        var ctx = new PostgresSquashGenerationContext(
            squashName: name,
            squashVersion: toVersion,
            dataSource: dataSource,
            captureSnapshotAsync: capture.CaptureAsync );

        var canonicalizer = new PostgresSnapshotCanonicalizer();
        var dataOpClassifier = new PostgresDataOpClassifier();
        var strategy = new PgDumpSnapshotStrategy( canonicalizer, dataOpClassifier );

        Console.WriteLine( "[squash] generating ..." );
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions
        {
            LowerBound = fromVersion,
            UpperBound = toVersion
        } );
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
                    output, name, generated, fromVersion, toVersion, ctx, stopwatch.Elapsed,
                    expectedFleetVersions, overrideEntries );

                // Per Phase 7 Task 7.7 — optional source-file removal after
                // successful generation. Idempotent: refuses if originals
                // already gone unless --regenerate is also supplied.
                if ( parsed.HasFlag( "remove-originals" ) )
                {
                    var migrationsRoot = parsed.Optional( "migrations-root" );
                    if ( string.IsNullOrWhiteSpace( migrationsRoot ) )
                    {
                        Console.Error.WriteLine(
                            "[squash] --remove-originals requires --migrations-root <path-to-migration-source-files>." );
                        return 7;
                    }

                    var rc = RemoveOriginalSources(
                        migrationsRoot,
                        descriptors.Select( d => d.Attribute.Version ).ToArray(),
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
        SquashGenerationResult.Generated gen,
        long fromVersion,
        long toVersion,
        PostgresSquashGenerationContext ctx,
        TimeSpan elapsed,
        IReadOnlyDictionary<string, long> expectedFleetVersions,
        IReadOnlyList<SquashOverrideEntry> overrideEntries )
    {
        var sqlPath = Path.Combine( output, $"{name}.sql" );
        var metadataPath = Path.Combine( output, $"{name}.metadata.json" );
        var summaryPath = Path.Combine( output, $"{name}.summary.md" );

        await File.WriteAllTextAsync( sqlPath, gen.Content );

        var topology = (PostgresTopologySignature) gen.Topology;
        var toolVersion = typeof( SquashVerb ).Assembly.GetName().Version?.ToString( 3 ) ?? "1.0.0";

        var metadata = new SquashMetadata
        {
            ReplacesFromVersion = fromVersion,
            ReplacesToVersion = toVersion,
            ProviderId = "postgres",
            Topology = topology.Properties,
            CanonicalizerVersion = "postgres/1.0.0",
            ExpectedFleetVersions = expectedFleetVersions.ToDictionary( kv => kv.Key, kv => kv.Value ),
            SquashOverrides = overrideEntries.ToArray(),
            CodegenToolVersion = $"hyperbee-migrations/{toolVersion}",
            GeneratedAt = DateTimeOffset.UtcNow
        };

        await File.WriteAllTextAsync( metadataPath, JsonSerializer.Serialize( metadata, new JsonSerializerOptions { WriteIndented = true } ) );

        var summary =
            $"# Squash {fromVersion}..{toVersion}\n\n" +
            $"- Generated: {metadata.GeneratedAt:O}\n" +
            $"- Tool: {metadata.CodegenToolVersion}\n" +
            $"- Topology: server_major={topology.ServerMajor}, encoding={topology.ServerEncoding}, " +
            $"extensions=[{string.Join( ", ", topology.Extensions )}]\n" +
            $"- Replaces ({gen.Replaces.Count} versions): {string.Join( ", ", gen.Replaces )}\n" +
            $"- Elapsed: {elapsed.TotalSeconds:F2}s\n\n" +
            $"## Diagnostics\n\n" +
            (gen.Diagnostics.Count == 0 ? "_(none)_\n" : string.Join( "\n", gen.Diagnostics.Select( d => $"- {d}" ) ) + "\n");
        await File.WriteAllTextAsync( summaryPath, summary );

        Console.WriteLine();
        Console.WriteLine( $"[squash] OK: emitted {gen.Replaces.Count} replaced versions; elapsed {elapsed.TotalSeconds:F2}s" );
        Console.WriteLine( $"  {sqlPath}" );
        Console.WriteLine( $"  {metadataPath}" );
        Console.WriteLine( $"  {summaryPath}" );
        if ( gen.Diagnostics.Count > 0 )
        {
            Console.WriteLine();
            Console.WriteLine( $"[squash] {gen.Diagnostics.Count} diagnostic(s) — review {summaryPath} before applying." );
        }
    }

    private static int Fail( string message )
    {
        Console.Error.WriteLine( $"hyperbee-migrations squash: {message}" );
        Console.Error.WriteLine();
        PrintHelp();
        return 2;
    }

    /// <summary>
    /// Phase 7 Task 7.7 — remove the original migration source files for
    /// versions subsumed by the squash. Idempotent: refuses (returns code 7)
    /// when the files are already gone unless <paramref name="regenerate"/>
    /// is true. Searches by filename pattern <c>*&lt;version&gt;*</c> in
    /// <paramref name="migrationsRoot"/> recursively.
    /// </summary>
    private static int RemoveOriginalSources( string migrationsRoot, long[] versions, bool regenerate )
    {
        if ( !Directory.Exists( migrationsRoot ) )
        {
            Console.Error.WriteLine( $"[squash] --migrations-root `{migrationsRoot}` does not exist." );
            return 7;
        }

        var foundByVersion = new Dictionary<long, List<string>>();
        foreach ( var version in versions )
        {
            // Match common naming conventions: <version>-<name>.cs,
            // <version>_<name>.cs, Migration_<version>.cs, etc. We anchor on
            // the version's digits being delimited so 1000 doesn't match 10000.
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

        // Per-name reasons: --reason-stranding "name=text" (>= 20 chars).
        // Each name listed in --accept-stranding must have a reason.
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

    private static void PrintHelp()
    {
        Console.WriteLine(
            "Usage: hyperbee-migrations squash \\\n" +
            "         --provider postgres \\\n" +
            "         --connection \"Host=...;Database=...;Username=...;Password=...\" \\\n" +
            "         --range <fromVersion>-<toVersion> \\\n" +
            "         --output <directory> \\\n" +
            "         --assembly <path-to-MyApp.Migrations.dll> \\\n" +
            "         [--name Squash_<toVersion>] \\\n" +
            "         [--fleet-manifest <fleet.yml>] \\\n" +
            "         [--accept-stranding name1,name2 --reason-stranding name1=\"...\" --reason-stranding name2=\"...\"] \\\n" +
            "         [--strand-ticket-id FLEET-1234 --strand-owner ops@example.com] \\\n" +
            "         [--remove-originals [--regenerate]]" );
    }
}

/// <summary>
/// Loads <see cref="MigrationDescriptor"/>s directly from an assembly's
/// reflected <see cref="MigrationAttribute"/> annotations, filtered by an
/// inclusive version range. CLI-specific helper: the runtime
/// <see cref="MigrationRunner"/> uses its own discovery; the squash CLI
/// only needs the projection.
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
