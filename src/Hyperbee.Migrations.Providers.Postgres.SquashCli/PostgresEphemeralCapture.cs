using Hyperbee.Migrations.Providers.Postgres.Squash;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Hyperbee.Migrations.Providers.Postgres.SquashCli;

/// <summary>
/// Postgres snapshot capture concrete: spins an ephemeral
/// <c>postgres:&lt;major&gt;-alpine</c> container per the topology signature
/// (per ADR-0019 A10 server-version-matched), applies migrations through
/// the requested upper bound via a caller-supplied delegate, runs
/// <c>pg_dump --schema-only</c> via <c>docker exec</c>, captures sequence
/// <c>last_value</c>s, and returns a <see cref="SnapshotCaptureResult"/>.
/// Per A18, the container is torn down after each capture.
/// </summary>
public sealed class PostgresEphemeralCapture : IAsyncDisposable
{
    private readonly Func<NpgsqlDataSource, long, CancellationToken, Task> _applyMigrations;

    /// <param name="applyMigrations">
    /// Caller-supplied callback that applies the operator's migration
    /// assembly through the requested upper version. Typically wraps the
    /// discovered <see cref="IMigrationHost"/>'s <c>ConfigureAsync</c>
    /// against an Npgsql connection bound to the ephemeral container.
    /// </param>
    public PostgresEphemeralCapture(
        Func<NpgsqlDataSource, long, CancellationToken, Task> applyMigrations )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
    }

    /// <summary>Captures one snapshot per the supplied request.</summary>
    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        CancellationToken cancellationToken )
    {
        var image = $"postgres:{request.RequiredTopology.ServerMajor}-alpine";

        var container = new PostgreSqlBuilder( image )
            .WithDatabase( "squashdb" )
            .WithUsername( "squash" )
            .WithPassword( "squash" )
            .WithCleanUp( true )
            .Build();

        try
        {
            await container.StartAsync( cancellationToken ).ConfigureAwait( false );

            await using ( var dataSource = NpgsqlDataSource.Create( container.GetConnectionString() ) )
            {
                await _applyMigrations( dataSource, request.UpToVersion, cancellationToken ).ConfigureAwait( false );
            }

            var dump = await DumpSchemaAsync( container, cancellationToken ).ConfigureAwait( false );
            var sequenceLastValues = await CaptureSequencesAsync( container, cancellationToken ).ConfigureAwait( false );

            return new SnapshotCaptureResult( dump, sequenceLastValues );
        }
        finally
        {
            await container.DisposeAsync().ConfigureAwait( false );
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> DumpSchemaAsync(
        PostgreSqlContainer container, CancellationToken cancellationToken )
    {
        var result = await container.ExecAsync( new[]
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
        PostgreSqlContainer container, CancellationToken cancellationToken )
    {
        await using var dataSource = NpgsqlDataSource.Create( container.GetConnectionString() );
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
