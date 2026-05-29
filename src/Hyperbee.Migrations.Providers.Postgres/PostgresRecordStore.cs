using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Hyperbee.Migrations.Providers.Postgres;

internal class PostgresRecordStore : IMigrationRecordStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresMigrationOptions _options;
    private readonly ILogger<PostgresRecordStore> _logger;

    public PostgresRecordStore(
        NpgsqlDataSource dataSource,
        PostgresMigrationOptions options,
        ILogger<PostgresRecordStore> logger )
    {
        _dataSource = dataSource;
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        // wait for system ready
        _logger.LogDebug( "Running {action}", nameof( InitializeAsync ) );

        var createCommand = _dataSource.CreateCommand( CreateMigrationTable() );
        await createCommand.ExecuteNonQueryAsync( cancellationToken );
    }

    public async Task<IDisposable> CreateLockAsync()
    {
        _logger.LogDebug( "Running {action}", nameof( CreateLockAsync ) );

        var getLockCommand = _dataSource.CreateCommand( GetMigrationLock() );
        var migrationLock = await getLockCommand.ExecuteScalarAsync();
        if ( migrationLock is DateTimeOffset releaseOn )
        {
            _logger.LogWarning( "{action} Lock already exists", nameof( CreateLockAsync ) );

            if ( releaseOn < DateTime.UtcNow )
            {
                _logger.LogInformation( "{action} Lock expired on {releaseOn}", nameof( CreateLockAsync ), releaseOn );
                var command = _dataSource.CreateCommand( DeleteMigrationLock() );
                command.ExecuteNonQuery();
            }
            else
            {
                throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable." );
            }
        }

        try
        {
            var command = _dataSource.CreateCommand( InsertMigrationLock( _options.LockMaxLifetime ) );
            await command.ExecuteNonQueryAsync();
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "{action} unable to create database lock", nameof( CreateLockAsync ) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ex );
        }

        return new Disposable( () =>
        {
            _logger.LogInformation( "{action} disposing lock", nameof( CreateLockAsync ) );

            try
            {
                var command = _dataSource.CreateCommand( DeleteMigrationLock() );
                command.ExecuteNonQuery();
            }
            catch ( Exception ex )
            {
                _logger.LogCritical( ex, "{action} unable to remove database lock", nameof( CreateLockAsync ) );
                throw;
            }
        } );
    }

    public async Task<bool> ExistsAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ExistsAsync ), recordId );

        var command = _dataSource.CreateCommand( GetMigrationRecord( recordId ) );
        var id = await command.ExecuteScalarAsync();

        _logger.LogDebug( "{action} found `{recordId}`", nameof( ExistsAsync ), id );

        return id != null;
    }

    public async Task<MigrationRecord> ReadAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ReadAsync ), recordId );

        var command = _dataSource.CreateCommand( GetMigrationRecordFull( recordId ) );
        await using var reader = await command.ExecuteReaderAsync();

        if ( !await reader.ReadAsync() )
            return null;

        // checksum (NULL on pre-checksum-era rows), kind (defaults to 0 = Migration),
        // replaces (NULL or empty array on pre-squash-era rows)
        var checksum = reader.IsDBNull( 2 ) ? null : reader.GetString( 2 );
        var kind = (MigrationRecordKind) reader.GetInt16( 3 );
        var replaces = reader.IsDBNull( 4 )
            ? Array.Empty<long>()
            : reader.GetFieldValue<long[]>( 4 );

        var record = new MigrationRecord
        {
            Id = reader.GetString( 0 ),
            RunOn = new DateTimeOffset( reader.GetDateTime( 1 ), TimeSpan.Zero ),
            Checksum = checksum,
            Kind = kind,
            Replaces = replaces
        };

        record.EnsureLedgerIntegrity();
        return record;
    }

    public async Task DeleteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( DeleteAsync ), recordId );

        var command = _dataSource.CreateCommand( DeleteMigrationRecord( recordId ) );
        await command.ExecuteNonQueryAsync();
    }

    public async Task WriteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( WriteAsync ), recordId );

        var command = _dataSource.CreateCommand( InsertMigrationRecord( recordId ) );
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlySet<string>> IntersectWithAppliedAsync(
        IEnumerable<string> candidateIds,
        CancellationToken cancellationToken = default )
    {
        if ( candidateIds == null )
            throw new ArgumentNullException( nameof( candidateIds ) );

        var ids = candidateIds as string[] ?? candidateIds.ToArray();
        var found = new HashSet<string>( StringComparer.Ordinal );
        if ( ids.Length == 0 )
            return found;

        // Single round-trip realtime read per ADR-0019 Phase 3. Postgres treats
        // = ANY(@ids) as a parameterized array predicate that the planner can
        // satisfy with a primary-key index scan (or Bitmap Heap on large sets).
        var sql = $"SELECT record_id FROM {MigrationTableName} WHERE record_id = ANY(@ids)";
        await using var cmd = _dataSource.CreateCommand( sql );
        cmd.Parameters.AddWithValue( "ids", NpgsqlDbType.Array | NpgsqlDbType.Text, ids );

        await using var reader = await cmd.ExecuteReaderAsync( cancellationToken ).ConfigureAwait( false );
        while ( await reader.ReadAsync( cancellationToken ).ConfigureAwait( false ) )
            found.Add( reader.GetString( 0 ) );

        return found;
    }

    public async Task<IReadOnlySet<long>> IntersectWithSquashedAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken = default )
    {
        if ( versions == null )
            throw new ArgumentNullException( nameof( versions ) );

        var inputs = versions as long[] ?? versions.ToArray();
        var covered = new HashSet<long>();
        if ( inputs.Length == 0 )
            return covered;

        // Transitive squash satisfaction (ADR-0019 A6): a Squash row whose
        // replaces array contains v means v is satisfied even though no direct
        // Migration_<v> row exists. Postgres GIN-indexed `&&` (array overlap)
        // narrows to candidate rows; we then unnest and intersect against input.
        var sql =
            $"SELECT DISTINCT v FROM (" +
            $"  SELECT unnest(replaces) AS v FROM {MigrationTableName} WHERE kind = 1 AND replaces && @versions" +
            $") s WHERE v = ANY(@versions)";

        await using var cmd = _dataSource.CreateCommand( sql );
        cmd.Parameters.AddWithValue( "versions", NpgsqlDbType.Array | NpgsqlDbType.Bigint, inputs );

        await using var reader = await cmd.ExecuteReaderAsync( cancellationToken ).ConfigureAwait( false );
        while ( await reader.ReadAsync( cancellationToken ).ConfigureAwait( false ) )
            covered.Add( reader.GetInt64( 0 ) );

        return covered;
    }

    public async Task<WriteOutcome> WriteAsync(
        MigrationRecord record,
        WritePrecondition precondition = WritePrecondition.None,
        CancellationToken cancellationToken = default )
    {
        if ( record == null )
            throw new ArgumentNullException( nameof( record ) );

        record.EnsureLedgerIntegrity();

        _logger.LogDebug( "Running {action} (record-bearing) with `{recordId}` precondition={precondition}",
            nameof( WriteAsync ), record.Id, precondition );

        // MustNotExist: parameterized INSERT ... ON CONFLICT DO NOTHING; check rows-affected.
        // None: parameterized UPSERT.
        var sql = precondition == WritePrecondition.MustNotExist
            ? $"INSERT INTO {MigrationTableName} (record_id, run_on, checksum, kind, replaces) " +
              "VALUES (@id, @runOn, @checksum, @kind, @replaces) ON CONFLICT (record_id) DO NOTHING"
            : $"INSERT INTO {MigrationTableName} (record_id, run_on, checksum, kind, replaces) " +
              "VALUES (@id, @runOn, @checksum, @kind, @replaces) " +
              "ON CONFLICT (record_id) DO UPDATE SET run_on = EXCLUDED.run_on, checksum = EXCLUDED.checksum, kind = EXCLUDED.kind, replaces = EXCLUDED.replaces";

        await using var cmd = _dataSource.CreateCommand( sql );
        cmd.Parameters.AddWithValue( "id", record.Id );
        cmd.Parameters.AddWithValue( "runOn", record.RunOn.UtcDateTime );
        cmd.Parameters.AddWithValue( "checksum", (object) record.Checksum ?? DBNull.Value );
        cmd.Parameters.AddWithValue( "kind", (short) record.Kind );

        var replacesParam = cmd.Parameters.Add( "replaces", NpgsqlDbType.Array | NpgsqlDbType.Bigint );
        replacesParam.Value = record.Replaces?.ToArray() ?? Array.Empty<long>();

        var rows = await cmd.ExecuteNonQueryAsync( cancellationToken ).ConfigureAwait( false );

        if ( precondition == WritePrecondition.None || rows == 1 )
            return WriteOutcome.Created;

        // MustNotExist + rows == 0: row already exists; compare checksums to decide.
        var existing = await ReadAsync( record.Id ).ConfigureAwait( false );
        if ( existing != null && string.Equals( existing.Checksum, record.Checksum, StringComparison.Ordinal ) )
            return WriteOutcome.AlreadyExistsBenign;

        return WriteOutcome.PreconditionFailed;
    }

    private string MigrationTableName => $"{_options.SchemaName}.{_options.TableName}";
    private string LockTableName => $"{_options.SchemaName}.{_options.LockName}";

    // Idempotent CREATE + ALTER: existing v2 deployments grow the new columns on first
    // initialize after upgrade. ADD COLUMN IF NOT EXISTS is supported in PG 9.6+.
    private string CreateMigrationTable() =>
        $"""
         CREATE SCHEMA IF NOT EXISTS {_options.SchemaName};

         CREATE TABLE IF NOT EXISTS {MigrationTableName}
         (
             record_id  character varying(255) PRIMARY KEY,
             run_on     timestamp without time zone NOT NULL
         );

         ALTER TABLE {MigrationTableName} ADD COLUMN IF NOT EXISTS checksum text NULL;
         ALTER TABLE {MigrationTableName} ADD COLUMN IF NOT EXISTS kind smallint NOT NULL DEFAULT 0;
         ALTER TABLE {MigrationTableName} ADD COLUMN IF NOT EXISTS replaces bigint[] NOT NULL DEFAULT ARRAY[]::bigint[];

         DO $$
         DECLARE def text;
         BEGIN
             SELECT pg_get_constraintdef( oid ) INTO def FROM pg_constraint
                 WHERE conname = '{_options.TableName}_kind_check'
                   AND conrelid = '{MigrationTableName}'::regclass;

             -- ADR-0027 / ADR-0021: the kind domain is now 0=Migration, 1=Squash,
             -- 2=Baseline, 3=Recovery, 4=InProgress. Older deployments carry the
             -- original CHECK (kind IN (0,1,2)) which rejects Recovery and the new
             -- InProgress sentinel. Add it widened when missing; drop+re-add when it
             -- predates value 4. Idempotent: once widened, def contains '4' and this
             -- is a no-op, so it is concurrency-tolerant across racing initializers.
             IF def IS NULL THEN
                 ALTER TABLE {MigrationTableName}
                     ADD CONSTRAINT {_options.TableName}_kind_check CHECK (kind IN (0, 1, 2, 3, 4));
             ELSIF def NOT LIKE '%4%' THEN
                 ALTER TABLE {MigrationTableName} DROP CONSTRAINT {_options.TableName}_kind_check;
                 ALTER TABLE {MigrationTableName}
                     ADD CONSTRAINT {_options.TableName}_kind_check CHECK (kind IN (0, 1, 2, 3, 4));
             END IF;
         END $$;

         CREATE TABLE IF NOT EXISTS {LockTableName}
         (
             id         integer PRIMARY KEY,
             locked_on  timestamp without time zone NOT NULL,
             release_on timestamp without time zone NOT NULL
         );
         """;

    private string GetMigrationRecord( string recordId ) => $"SELECT record_id FROM {MigrationTableName} WHERE record_id = '{recordId}'";

    private string GetMigrationRecordFull( string recordId ) => $"SELECT record_id, run_on, checksum, kind, replaces FROM {MigrationTableName} WHERE record_id = '{recordId}'";

    private string InsertMigrationRecord( string recordId ) => $"INSERT INTO {MigrationTableName} (record_id, run_on) VALUES ('{recordId}', NOW())";

    private string DeleteMigrationRecord( string recordId ) => $"DELETE FROM {MigrationTableName} WHERE record_id = '{recordId}'";

    private string GetMigrationLock() => $"SELECT release_on FROM {LockTableName} LIMIT 1";

    private string InsertMigrationLock( TimeSpan maxLifetime ) => $"INSERT INTO {LockTableName} (id, locked_on, release_on) VALUES (1, NOW(), NOW() + INTERVAL '{maxLifetime.TotalSeconds} SECONDS')";

    private string DeleteMigrationLock() => $"DELETE FROM {LockTableName}";


    private sealed class Disposable( Action dispose ) : IDisposable
    {
        private int _disposed;
        private Action Disposer { get; } = dispose;

        public void Dispose()
        {
            if ( Interlocked.CompareExchange( ref _disposed, 1, 0 ) == 0 )
                Disposer.Invoke();
        }
    }
}
