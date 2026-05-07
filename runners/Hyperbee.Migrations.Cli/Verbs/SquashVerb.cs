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

    private static void PrintHelp()
    {
        Console.WriteLine(
            "Usage: hyperbee-migrations squash \\\n" +
            "         --provider postgres \\\n" +
            "         --connection \"Host=...;Database=...;Username=...;Password=...\" \\\n" +
            "         --range <fromVersion>-<toVersion> \\\n" +
            "         --output <directory> \\\n" +
            "         --assembly <path-to-MyApp.Migrations.dll> \\\n" +
            "         [--name Squash_<toVersion>]" );
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
