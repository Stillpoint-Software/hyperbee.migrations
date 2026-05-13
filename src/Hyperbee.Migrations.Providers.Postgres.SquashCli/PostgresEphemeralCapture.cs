using Hyperbee.Migrations.Providers.Postgres.Squash;
using Hyperbee.Migrations.Squash.Cli;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres.SquashCli;

/// <summary>
/// Postgres snapshot capture orchestrator. Consumes an
/// <see cref="IEphemeralProvisioner"/> for the container lifecycle (default
/// <see cref="PostgresEphemeralProvisioner"/>); applies migrations through
/// a caller-supplied delegate; runs <c>pg_dump --schema-only</c> via
/// <c>docker exec</c>; returns a section-headered
/// <see cref="SnapshotCaptureResult"/>.
/// </summary>
public sealed class PostgresEphemeralCapture : IAsyncDisposable
{
    private readonly IEphemeralProvisioner _provisioner;
    private readonly Func<string, long, CancellationToken, Task> _applyMigrations;

    /// <param name="applyMigrations">
    /// Apply-through-upper-bound callback. Receives the ephemeral container's
    /// connection string (verbatim, with credentials) and the upper-bound
    /// version. Typically wraps the discovered <see cref="IMigrationHost"/>.
    /// IMPORTANT: the connection string must be passed through verbatim --
    /// re-deriving from <c>NpgsqlDataSource.ConnectionString</c> strips the
    /// password under Npgsql 10's secret-handling rules and breaks SCRAM
    /// auth on the ephemeral container.
    /// </param>
    /// <param name="provisioner">
    /// Optional alternate provisioner; defaults to
    /// <see cref="PostgresEphemeralProvisioner"/>.
    /// </param>
    public PostgresEphemeralCapture(
        Func<string, long, CancellationToken, Task> applyMigrations,
        IEphemeralProvisioner provisioner = null )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
        _provisioner = provisioner ?? new PostgresEphemeralProvisioner();
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        CancellationToken cancellationToken )
    {
        var hints = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["server-major"] = request.RequiredTopology.ServerMajor.ToString()
        };

        await using var rawFixture = await _provisioner.ProvisionAsync( hints, cancellationToken )
            .ConfigureAwait( false );

        // The provider's pg_dump-via-exec path needs the underlying
        // PostgreSqlContainer handle. The default provisioner returns a
        // PostgresEphemeralFixture carrying it; alternate provisioners
        // (third-party, sibling-container, etc.) must do the same.
        if ( rawFixture is not PostgresEphemeralFixture pgFixture )
        {
            throw new InvalidOperationException(
                $"IEphemeralProvisioner returned `{rawFixture?.GetType().FullName}`; " +
                $"PostgresEphemeralCapture requires `{nameof( PostgresEphemeralFixture )}` " +
                "(or a derived type) so the snapshot pipeline can run pg_dump via docker exec." );
        }

        // Apply migrations through the connection string verbatim. See
        // the docstring on _applyMigrations for the Npgsql 10 password-
        // stripping rationale.
        await _applyMigrations( pgFixture.ConnectionString, request.UpToVersion, cancellationToken )
            .ConfigureAwait( false );

        var dump = await DumpSchemaAsync( pgFixture, cancellationToken ).ConfigureAwait( false );
        var sequenceLastValues = await CaptureSequencesAsync( pgFixture, cancellationToken )
            .ConfigureAwait( false );

        return new SnapshotCaptureResult( dump, sequenceLastValues );
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> DumpSchemaAsync(
        PostgresEphemeralFixture fixture, CancellationToken cancellationToken )
    {
        var result = await fixture.Container.ExecAsync( new[]
        {
            "pg_dump",
            "--schema-only",
            "--no-owner",
            "--no-privileges",
            "-U", "squash",
            "-d", "squashdb"
        }, cancellationToken ).ConfigureAwait( false );

        if ( result.ExitCode != 0 )
            throw new InvalidOperationException(
                $"pg_dump exec failed (exit={result.ExitCode}). stderr:\n{result.Stderr}" );

        return result.Stdout;
    }

    private static async Task<IReadOnlyDictionary<string, long>> CaptureSequencesAsync(
        PostgresEphemeralFixture fixture, CancellationToken cancellationToken )
    {
        await using var dataSource = new NpgsqlDataSourceBuilder( fixture.ConnectionString ).Build();
        await using var conn = await dataSource.OpenConnectionAsync( cancellationToken ).ConfigureAwait( false );

        const string sql =
            "SELECT schemaname || '.' || sequencename AS qualified_name, last_value " +
            "FROM pg_sequences " +
            "WHERE schemaname NOT IN ('pg_catalog', 'information_schema') " +
            "ORDER BY qualified_name";

        var results = new SortedDictionary<string, long>( StringComparer.Ordinal );

        await using var cmd = new NpgsqlCommand( sql, conn );
        await using var reader = await cmd.ExecuteReaderAsync( cancellationToken ).ConfigureAwait( false );
        while ( await reader.ReadAsync( cancellationToken ).ConfigureAwait( false ) )
        {
            var name = reader.GetString( 0 );
            if ( reader.IsDBNull( 1 ) )
                continue;
            var lastValue = reader.GetInt64( 1 );
            results[name] = lastValue;
        }

        return results;
    }
}
