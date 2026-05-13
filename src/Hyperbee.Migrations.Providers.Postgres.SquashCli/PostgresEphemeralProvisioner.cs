using Hyperbee.Migrations.Squash.Cli;
using Testcontainers.PostgreSql;

namespace Hyperbee.Migrations.Providers.Postgres.SquashCli;

/// <summary>
/// Default Postgres <see cref="IEphemeralProvisioner"/>: spins an ephemeral
/// <c>postgres:&lt;server-major&gt;-alpine</c> container via
/// <see cref="PostgreSqlBuilder"/> (per ADR-0019 A10 server-version matched).
/// Reads <c>server-major</c> from the hints dictionary; defaults to <c>16</c>.
/// </summary>
public sealed class PostgresEphemeralProvisioner : IEphemeralProvisioner
{
    public async Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken )
    {
        var serverMajor = hints != null && hints.TryGetValue( "server-major", out var sm ) && !string.IsNullOrWhiteSpace( sm )
            ? sm
            : "16";
        var image = $"postgres:{serverMajor}-alpine";

        var container = new PostgreSqlBuilder( image )
            .WithDatabase( "squashdb" )
            .WithUsername( "squash" )
            .WithPassword( "squash" )
            .WithCleanUp( true )
            .Build();

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );
        return new PostgresEphemeralFixture( container );
    }
}

/// <summary>
/// Postgres-specific fixture exposing the underlying
/// <see cref="PostgreSqlContainer"/> so the capture orchestrator can run
/// <c>pg_dump</c> via <c>docker exec</c>. Disposal tears down the container.
/// </summary>
public sealed class PostgresEphemeralFixture : IEphemeralFixture
{
    public PostgreSqlContainer Container { get; }
    public string ConnectionString { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal PostgresEphemeralFixture( PostgreSqlContainer container )
    {
        Container = container;
        ConnectionString = container.GetConnectionString();
        Metadata = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["hostname"] = container.Hostname,
            ["database"] = "squashdb",
            ["username"] = "squash"
        };
    }

    public ValueTask DisposeAsync() => Container.DisposeAsync();
}
