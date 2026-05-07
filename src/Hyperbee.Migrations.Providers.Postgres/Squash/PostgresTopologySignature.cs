using Hyperbee.Migrations.Squash;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Postgres topology signature per ADR-0019 + A14. Captures the axes that
/// affect squash codegen determinism: server major, server minor, installed
/// extensions, collation provider, locale provider, server encoding.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsCompatibleWith"/> rules:
/// <list type="bullet">
///   <item>Server major version must match exactly. Postgres major releases
///         can change pg_dump output shape; mixing majors invalidates the
///         determinism gate.</item>
///   <item>Extension set must match exactly (presence, not version). Per
///         spike finding F5 the SET preamble shape is version-dependent;
///         extension presence drives the prerequisites file content.</item>
///   <item>Collation provider, locale provider, and server encoding must
///         match exactly. These affect identifier sort order in dumps.</item>
/// </list>
/// Server minor differences are tolerated — they don't affect dump shape
/// in any released minor (per Postgres release notes review).
/// </para>
/// </remarks>
public sealed record PostgresTopologySignature : ITopologySignature
{
    public const string ProviderIdValue = "postgres";

    public int SchemaVersion => 1;
    public string ProviderId => ProviderIdValue;

    public int ServerMajor { get; init; }
    public int ServerMinor { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();
    public string CollationProvider { get; init; } = "";
    public string LocaleProvider { get; init; } = "";
    public string ServerEncoding { get; init; } = "";

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["server_major"] = ServerMajor.ToString( System.Globalization.CultureInfo.InvariantCulture ),
        ["server_minor"] = ServerMinor.ToString( System.Globalization.CultureInfo.InvariantCulture ),
        ["extensions"] = string.Join( ",", Extensions.OrderBy( x => x, StringComparer.Ordinal ) ),
        ["collation_provider"] = CollationProvider,
        ["locale_provider"] = LocaleProvider,
        ["server_encoding"] = ServerEncoding
    };

    public bool IsCompatibleWith( ITopologySignature other, out string reason )
    {
        if ( other is not PostgresTopologySignature pg )
        {
            reason = $"signature is `{other?.ProviderId ?? "<null>"}`, not `{ProviderIdValue}`";
            return false;
        }

        if ( pg.ServerMajor != ServerMajor )
        {
            reason = $"server_major differs (this={ServerMajor}, other={pg.ServerMajor})";
            return false;
        }

        if ( !string.Equals( pg.CollationProvider, CollationProvider, StringComparison.Ordinal ) )
        {
            reason = $"collation_provider differs (this='{CollationProvider}', other='{pg.CollationProvider}')";
            return false;
        }

        if ( !string.Equals( pg.LocaleProvider, LocaleProvider, StringComparison.Ordinal ) )
        {
            reason = $"locale_provider differs (this='{LocaleProvider}', other='{pg.LocaleProvider}')";
            return false;
        }

        if ( !string.Equals( pg.ServerEncoding, ServerEncoding, StringComparison.Ordinal ) )
        {
            reason = $"server_encoding differs (this='{ServerEncoding}', other='{pg.ServerEncoding}')";
            return false;
        }

        var mine = new SortedSet<string>( Extensions, StringComparer.Ordinal );
        var theirs = new SortedSet<string>( pg.Extensions, StringComparer.Ordinal );
        if ( !mine.SetEquals( theirs ) )
        {
            var missingHere = string.Join( ",", theirs.Except( mine ) );
            var extraHere = string.Join( ",", mine.Except( theirs ) );
            reason =
                $"extension set differs (other has [{(missingHere.Length > 0 ? missingHere : "<none>")}], " +
                $"this has [{(extraHere.Length > 0 ? extraHere : "<none>")}])";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Captures the live Postgres server's topology axes via system catalog
    /// probes. Use the same connection that owns the migration ledger so the
    /// signature reflects the operator's effective deployment.
    /// </summary>
    public static async Task<PostgresTopologySignature> CaptureAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default )
    {
        if ( connection == null )
            throw new ArgumentNullException( nameof( connection ) );

        if ( connection.State != System.Data.ConnectionState.Open )
            await connection.OpenAsync( cancellationToken ).ConfigureAwait( false );

        // server_version_num: e.g., 160013 -> major=16, minor=13
        var versionNum = await ScalarLongAsync(
            connection, "SHOW server_version_num", cancellationToken ).ConfigureAwait( false );

        var serverMajor = (int) (versionNum / 10000);
        var serverMinor = (int) (versionNum % 10000);

        var serverEncoding = await ScalarStringAsync(
            connection, "SHOW server_encoding", cancellationToken ).ConfigureAwait( false );

        // collation_provider / locale_provider: introduced in PG 15. For older
        // servers we default to "libc" (the historical only-option). Probing
        // pg_database fields keeps the signature aligned with the actual
        // operational locale.
        var (collationProvider, localeProvider) = await ProbeLocaleProvidersAsync(
            connection, cancellationToken ).ConfigureAwait( false );

        var extensions = await ListExtensionsAsync(
            connection, cancellationToken ).ConfigureAwait( false );

        return new PostgresTopologySignature
        {
            ServerMajor = serverMajor,
            ServerMinor = serverMinor,
            Extensions = extensions,
            CollationProvider = collationProvider,
            LocaleProvider = localeProvider,
            ServerEncoding = serverEncoding
        };
    }

    private static async Task<long> ScalarLongAsync( NpgsqlConnection cn, string sql, CancellationToken ct )
    {
        await using var cmd = new NpgsqlCommand( sql, cn );
        var v = await cmd.ExecuteScalarAsync( ct ).ConfigureAwait( false );
        return v switch
        {
            long l => l,
            int i => i,
            string s => long.Parse( s, System.Globalization.CultureInfo.InvariantCulture ),
            _ => throw new MigrationException( $"Unexpected result type from `{sql}`: {v?.GetType().Name ?? "<null>"}" )
        };
    }

    private static async Task<string> ScalarStringAsync( NpgsqlConnection cn, string sql, CancellationToken ct )
    {
        await using var cmd = new NpgsqlCommand( sql, cn );
        var v = await cmd.ExecuteScalarAsync( ct ).ConfigureAwait( false );
        return v?.ToString() ?? "";
    }

    private static async Task<(string collationProvider, string localeProvider)> ProbeLocaleProvidersAsync(
        NpgsqlConnection cn, CancellationToken ct )
    {
        // pg_database.datlocprovider exists from PG 15+. Probe pg_attribute
        // first so we don't error on older servers.
        const string probeSql =
            "SELECT 1 FROM pg_attribute " +
            "WHERE attrelid = 'pg_database'::regclass AND attname = 'datlocprovider' AND NOT attisdropped";

        await using ( var probe = new NpgsqlCommand( probeSql, cn ) )
        {
            var hasColumn = (await probe.ExecuteScalarAsync( ct ).ConfigureAwait( false )) != null;
            if ( !hasColumn )
                return (collationProvider: "libc", localeProvider: "libc");
        }

        const string sql =
            "SELECT datcollate, datlocprovider::text " +
            "FROM pg_database WHERE datname = current_database()";

        await using var cmd = new NpgsqlCommand( sql, cn );
        await using var reader = await cmd.ExecuteReaderAsync( ct ).ConfigureAwait( false );
        if ( !await reader.ReadAsync( ct ).ConfigureAwait( false ) )
            return (collationProvider: "libc", localeProvider: "libc");

        var datCollate = reader.IsDBNull( 0 ) ? "" : reader.GetString( 0 );
        var locProvider = reader.IsDBNull( 1 ) ? "libc" : reader.GetString( 1 );

        return (collationProvider: datCollate, localeProvider: locProvider);
    }

    private static async Task<IReadOnlyList<string>> ListExtensionsAsync(
        NpgsqlConnection cn, CancellationToken ct )
    {
        const string sql = "SELECT extname FROM pg_extension ORDER BY extname";
        var names = new List<string>();

        await using var cmd = new NpgsqlCommand( sql, cn );
        await using var reader = await cmd.ExecuteReaderAsync( ct ).ConfigureAwait( false );
        while ( await reader.ReadAsync( ct ).ConfigureAwait( false ) )
            names.Add( reader.GetString( 0 ) );
        return names;
    }
}
