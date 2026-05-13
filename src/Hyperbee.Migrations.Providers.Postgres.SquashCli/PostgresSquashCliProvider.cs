using Hyperbee.Migrations.Providers.Postgres.Squash;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres.SquashCli;

/// <summary>
/// Postgres implementation of <see cref="ISquashCliProvider"/>. Discovered by
/// the CLI via the migration assembly's reference closure (per ADR-0024);
/// requires only a public parameterless constructor.
/// </summary>
public sealed class PostgresSquashCliProvider : ISquashCliProvider
{
    private readonly IEphemeralProvisioner _provisioner;

    public string ProviderId => "postgres";
    public string SquashFileExtension => ".sql";

    /// <summary>
    /// Default ctor used by <see cref="CliProviderRegistry.Discover"/>
    /// (requires public parameterless ctor). Wires the default
    /// <see cref="PostgresEphemeralProvisioner"/>.
    /// </summary>
    public PostgresSquashCliProvider()
        : this( provisioner: null )
    {
    }

    /// <summary>
    /// Test / DI ctor that lets the caller swap the provisioner (e.g.
    /// integration tests inject a real <see cref="PostgresEphemeralProvisioner"/>;
    /// a third-party host could plug in a sibling-container variant).
    /// </summary>
    public PostgresSquashCliProvider( IEphemeralProvisioner provisioner )
    {
        _provisioner = provisioner ?? new PostgresEphemeralProvisioner();
    }

    public async Task<SquashGenerationResult> GenerateAsync(
        SquashCliContext context,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( context );

        // Build the ephemeral capture pipeline. The apply delegate routes
        // through the discovered IMigrationHost so the operator's own DI
        // wiring (Add{Provider}Migrations + their MigrationOptions
        // configuration) is the single source of truth -- no static
        // ApplyToDataSourceAsync reflection convention (RB-4 closed).
        var capture = new PostgresEphemeralCapture(
            applyMigrations: async ( ephemeralConnStr, upTo, ct ) =>
            {
                await ApplyMigrationsViaHostAsync(
                    context.MigrationHost,
                    ephemeralConnStr,
                    upTo,
                    ct ).ConfigureAwait( false );
            },
            provisioner: _provisioner );

        // Use NpgsqlDataSourceBuilder so credentials in the connection string
        // (Username/Password) are honored end-to-end through SCRAM-SHA-256
        // handshake.
        var dsBuilder = new NpgsqlDataSourceBuilder( context.ConnectionString );
        await using var liveDataSource = dsBuilder.Build();

        var ctx = new PostgresSquashGenerationContext(
            squashName: context.SquashName,
            squashVersion: context.ToVersion,
            dataSource: liveDataSource,
            captureSnapshotAsync: capture.CaptureAsync );

        var canonicalizer = new PostgresSnapshotCanonicalizer();
        var dataOpClassifier = new PostgresDataOpClassifier();
        var strategy = new PgDumpSnapshotStrategy( canonicalizer, dataOpClassifier );

        return await strategy.GenerateAsync( ctx, context.Descriptors, context.Options )
            .ConfigureAwait( false );
    }

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string sourceDirectory )
    {
        if ( string.IsNullOrWhiteSpace( sourceDirectory ) )
            return Array.Empty<MigrationSourceVerdict>();

        var verdicts = PostgresMigrationSourceScanner.Scan( sourceDirectory );
        return verdicts
            .Select( v => new MigrationSourceVerdict
            {
                FilePath = v.FilePath,
                ClassName = v.ClassName,
                RequiresAnnotation = v.RequiresAnnotation,
                DataOpHits = v.DataOpHits,
                NonDeterminismHits = v.NonDeterminismHits
            } )
            .ToArray();
    }

    public async Task<long> ProbeLastAppliedVersionAsync(
        string liveConnectionString,
        IReadOnlyDictionary<string, string> providerOptions,
        CancellationToken cancellationToken )
    {
        // RB-3: read schema + table from per-environment overrides rather
        // than hardcoding `public.migrations`. Operator can supply
        // `schema-name` and `table-name` under the fleet manifest's
        // `topology:` for the environment; both default to the values
        // PostgresMigrationOptions ships with.
        var schemaName = providerOptions != null
            && providerOptions.TryGetValue( "schema-name", out var schema )
            && !string.IsNullOrWhiteSpace( schema )
            ? schema
            : "migration"; // matches PostgresMigrationOptions default
        var tableName = providerOptions != null
            && providerOptions.TryGetValue( "table-name", out var table )
            && !string.IsNullOrWhiteSpace( table )
            ? table
            : "migrations";

        // Sanitize: schema/table identifiers come from operator config; quote
        // them via Npgsql.NpgsqlCommand's identifier quoting to prevent SQL
        // injection while preserving case sensitivity.
        var qualifiedTable = QuoteIdentifier( schemaName ) + "." + QuoteIdentifier( tableName );

        await using var dataSource = new NpgsqlDataSourceBuilder( liveConnectionString ).Build();
        await using var connection = await dataSource.OpenConnectionAsync( cancellationToken ).ConfigureAwait( false );

        var sql =
            "SELECT MAX(CAST(SPLIT_PART(record_id, '.', 2) AS bigint)) " +
            $"FROM {qualifiedTable} " +
            "WHERE record_id ~ '^record\\.\\d+\\.'";

        try
        {
            await using var cmd = new NpgsqlCommand( sql, connection );
            var result = await cmd.ExecuteScalarAsync( cancellationToken ).ConfigureAwait( false );
            if ( result is null || result is DBNull )
                return 0;
            return Convert.ToInt64( result, System.Globalization.CultureInfo.InvariantCulture );
        }
        catch ( PostgresException ex ) when ( ex.SqlState == "42P01" )
        {
            // ledger table doesn't exist yet -- treat as empty
            return 0;
        }
    }

    private static string QuoteIdentifier( string identifier )
    {
        // PostgreSQL identifier quoting: double-quoted; embedded quotes
        // doubled. Rejects nulls + control chars defensively.
        if ( identifier == null )
            throw new ArgumentNullException( nameof( identifier ) );
        foreach ( var c in identifier )
        {
            if ( char.IsControl( c ) )
                throw new ArgumentException(
                    $"Invalid identifier `{identifier}`: control characters are not permitted.", nameof( identifier ) );
        }
        return "\"" + identifier.Replace( "\"", "\"\"" ) + "\"";
    }

    private static async Task ApplyMigrationsViaHostAsync(
        IMigrationHost host,
        string ephemeralConnectionString,
        long upToVersion,
        CancellationToken cancellationToken )
    {
        // Per ADR-0024: every migration assembly contains exactly one public
        // IMigrationHost implementation. SquashVerb hands us the already-
        // activated host; we call ConfigureAsync against the ephemeral
        // container's connection string, with an override that bounds the
        // run to <= upToVersion. RB-4 close-out: this path is the
        // replacement for the v1 ApplyToDataSourceAsync static reflection
        // convention -- the host class is the single supported integration
        // point. The connection string MUST be passed verbatim from the
        // fixture -- Npgsql 10's NpgsqlDataSource.ConnectionString strips
        // the password under its secret-handling rules, breaking SCRAM
        // auth on the ephemeral container.
        ArgumentNullException.ThrowIfNull( host );

        var ctx = new MigrationHostContext( ephemeralConnectionString )
        {
            OverrideOptions = opts => opts.ToVersion = upToVersion
        };

        var serviceProvider = await host.ConfigureAsync( ctx, cancellationToken ).ConfigureAwait( false );
        try
        {
            // Resolve the typed Postgres runner: works in both single- and
            // multi-provider hosts (the base MigrationRunner throws in
            // multi-provider hosts per ADR-0023).
            var runner = serviceProvider.GetRequiredService<PostgresMigrationRunner>();
            await runner.RunAsync( cancellationToken ).ConfigureAwait( false );
        }
        finally
        {
            if ( serviceProvider is IAsyncDisposable iad ) await iad.DisposeAsync().ConfigureAwait( false );
            else if ( serviceProvider is IDisposable d ) d.Dispose();
        }
    }
}
