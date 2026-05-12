# Hyperbee.Migrations.Providers.Postgres

The PostgreSQL provider for [Hyperbee.Migrations](https://github.com/Stillpoint-Software/hyperbee.migrations) -- a versioned, journaled migration framework for .NET. Targets PostgreSQL 14+.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.Postgres
```

## Quick start

```csharp
using Hyperbee.Migrations.Providers.Postgres;

var builder = WebApplication.CreateBuilder( args );

// Register the Npgsql data source the migration runner reads from.
builder.Services.AddNpgsqlDataSource( builder.Configuration.GetConnectionString( "Migrations" ) );

builder.Services.AddPostgresMigrations( opts =>
{
    opts.SchemaName     = "migration";  // ledger schema (default: "migration")
    opts.LockingEnabled = true;
} );

var app = builder.Build();

using ( var scope = app.Services.CreateScope() )
{
    // Single-provider hosts can resolve the base MigrationRunner.
    // For multi-provider hosts (ADR-0023), resolve PostgresMigrationRunner directly.
    var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    await runner.RunAsync( app.Lifetime.ApplicationStopping );
}

app.Run();
```

A migration uses raw `.sql` files via `PostgresResourceRunner`:

```csharp
[Migration( 20260101_001 )]
public class CreateUsersTable( PostgresResourceRunner<CreateUsersTable> runner ) : Migration
{
    public override Task UpAsync( CancellationToken ct = default )
        => runner.AllSqlFromAsync( ct );
}
```

The companion file `Resources/20260101_001_CreateUsersTable.sql` ships in the assembly via `<EmbeddedResource>`.

## Documentation

For configuration reference, resource layout, locking semantics (session-level advisory lock per [ADR-0005](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0005-provider-native-distributed-locking.md)), and operational guidance, see:

**https://stillpoint-software.github.io/hyperbee.migrations/postgresql.html**

A working sample lives in [`runners/samples/Hyperbee.Migrations.Postgres.Samples/`](https://github.com/Stillpoint-Software/hyperbee.migrations/tree/main/runners/samples/Hyperbee.Migrations.Postgres.Samples).

## Multi-provider hosts

For applications that host more than one provider in the same `IServiceCollection`, resolve `PostgresMigrationRunner` directly rather than the base `MigrationRunner` (which throws in multi-provider hosts per [ADR-0023](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0023-multi-runner-not-meta-runner.md)). See the [multi-provider hosts guide](https://stillpoint-software.github.io/hyperbee.migrations/multi-provider-hosts.html) for the failure-isolation, parallel-composition, and expand/contract patterns.

## License

[MIT](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/LICENSE)
