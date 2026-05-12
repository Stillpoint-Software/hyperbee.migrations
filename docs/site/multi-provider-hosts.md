---
layout: default
title: Multi-Provider Hosts
nav_order: 8
---

# Multi-Provider Hosts

A single application can register and run migrations for more than one provider in the same `IServiceCollection`. Each provider keeps its own ledger, its own lock, and its own discovery scope -- they do not interfere with each other. This page shows the registration shape, the recommended invocation pattern, and what to do when a feature touches more than one store.

> The behavior described here ships with [ADR-0023](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0023-multi-runner-not-meta-runner.md). Single-provider hosts continue to work unchanged.

## When to use a multi-provider host

Reach for a multi-provider host when one application owns data in more than one store and you want a single boot-time entry point for migrations:

- An app that uses **PostgreSQL** for transactional data and **MongoDB** for documents.
- An app that uses **Aerospike** for hot-path caching and **OpenSearch** for search indices.
- A platform service that owns **multiple stores** at once and needs each schema/state evolution to ship together.

If your app uses one provider, none of this applies -- keep using `GetRequiredService<MigrationRunner>()` as before.

## Registration

Each `Add{Provider}Migrations` call registers a typed runner: `OpenSearchMigrationRunner`, `AerospikeMigrationRunner`, etc. They coexist on the same `IServiceCollection` -- there is no shadowing.

```csharp
using Hyperbee.Migrations.Providers.Aerospike;
using Hyperbee.Migrations.Providers.OpenSearch;

var builder = WebApplication.CreateBuilder( args );

builder.Services
    .WithProductionDefaults()
    .AddOpenSearchMigrations( opts =>
    {
        opts.LedgerIndex   = ".migrations";
        opts.LockIndex     = ".migrations-lock";
        opts.LockName      = builder.Configuration["Migrations:LockName"] ?? "host-lock";
        opts.Profiles      = ["bootstrap", "production"];
    } );

builder.Services.AddAerospikeMigrations( opts =>
{
    opts.Namespace      = "test";
    opts.MigrationSet   = "SchemaMigrations";
    opts.LockSet        = "SchemaMigrationLocks";
    opts.LockName       = "migration_lock";
    opts.LockingEnabled = true;
    opts.Profiles       = ["bootstrap"];
} );

var app = builder.Build();
```

Profiles, discovery assemblies, lock tuning, and connection settings are all per-provider. Configure each runner the way you would in a single-provider host -- nothing changes about the per-provider options.

## Sequential invocation (recommended default)

Resolve each runner by its concrete type and invoke it. The library deliberately does **not** ship a coordinator type -- write the foreach loop so that the failure semantics are visible at the call site.

```csharp
using ( var scope = app.Services.CreateScope() )
{
    var sp     = scope.ServiceProvider;
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var ct     = app.Lifetime.ApplicationStopping;

    var os  = sp.GetRequiredService<OpenSearchMigrationRunner>();
    var aer = sp.GetRequiredService<AerospikeMigrationRunner>();

    var failures = new List<Exception>();

    try
    {
        await os.RunAsync( ct );
        logger.LogInformation( "OpenSearch migrations completed" );
    }
    catch ( Exception ex )
    {
        failures.Add( ex );
        logger.LogError( ex, "OpenSearch migrations failed" );
    }

    try
    {
        await aer.RunAsync( ct );
        logger.LogInformation( "Aerospike migrations completed" );
    }
    catch ( Exception ex )
    {
        failures.Add( ex );
        logger.LogError( ex, "Aerospike migrations failed" );
    }

    if ( failures.Count > 0 )
        throw new AggregateException( "One or more provider migrations failed", failures );
}

app.Run();
```

This shape is deliberate:

- **Per-provider try/catch** keeps a failure in one provider from short-circuiting the others. If your operational policy is "stop on first failure," remove the second `try` and let the exception propagate -- but that decision should be conscious, not the default.
- **Logging carries provider context.** `OpenSearch migrations failed` is unambiguous; a generic `Migrations failed` is not.
- **`AggregateException` at the end** ensures both errors surface if both providers fail. Throwing on the first error swallows the second.
- **`ApplicationStopping` cancellation token** lets a SIGTERM during host startup cancel mid-migration cleanly. Each provider's lock release is provider-native and runs on cancellation.

## Parallel invocation (when providers are disjoint)

If the two providers share no application-layer dependency between their migration sets, the invocation can be parallelized. Each provider's lock acquisition is independent (per [ADR-0005](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0005-provider-native-distributed-locking.md)), so there is no cross-provider lock contention.

```csharp
using ( var scope = app.Services.CreateScope() )
{
    var sp = scope.ServiceProvider;
    var ct = app.Lifetime.ApplicationStopping;

    var os  = sp.GetRequiredService<OpenSearchMigrationRunner>();
    var aer = sp.GetRequiredService<AerospikeMigrationRunner>();

    // Task.WhenAll surfaces an AggregateException naturally if either fails.
    await Task.WhenAll( os.RunAsync( ct ), aer.RunAsync( ct ) );
}
```

Use parallel invocation when both providers cold-start slowly and you want to overlap the wait. Sum-of-bootstraps becomes max-of-bootstraps. Don't use it when the second provider's migrations depend on observable state from the first.

## What does not work in a multi-provider host

Resolving the **base** `MigrationRunner` type in a multi-provider host throws with a clear message:

```csharp
// In a multi-provider host this throws:
//   InvalidOperationException: "Multiple providers registered ... Resolve {Provider}MigrationRunner explicitly."
var runner = sp.GetRequiredService<MigrationRunner>();
```

The same applies to the base `MigrationOptions` and `IMigrationRecordStore` resolutions. Use the typed subclasses (`OpenSearchMigrationRunner`, `AerospikeMigrationRunner`, `PostgresMigrationRunner`, `MongoDBMigrationRunner`, `CouchbaseMigrationRunner`) instead.

The reason this fails loud rather than picking a winner: if the base resolution silently bound to the first-registered provider, a forgotten `GetRequiredService<MigrationRunner>()` in a sample, controller, or test fixture would silently run only one provider's migrations and skip the other. Failing loud forces the typed resolution to be visible at every call site.

Single-provider hosts are unaffected -- `GetRequiredService<MigrationRunner>()` continues to resolve to the typed subclass.

## Coordinating changes that touch both stores

Migrations are infrastructure: each runner owns one store's ledger and one store's locking. There is no built-in coordinator that runs a migration "atomically across providers" -- atomicity across heterogeneous stores is unsolvable without distributed transactions, which the providers do not share.

Use the **expand/contract pattern** at the application layer instead:

1. **Expand.** Add the new shape in each store, leaving the old shape in place. Each store's migration is independent and can succeed without the other.
2. **Dual-write behind a flag.** Application code writes to both shapes during the rollout window.
3. **Cut over.** Once the flag is fully on across the fleet and the new shape is well-populated, switch reads.
4. **Contract.** Once nothing reads the old shape, ship a contract migration in each store to drop it.

A worked example -- adding a `user_profiles` aggregate that lives partly in PostgreSQL and partly in MongoDB:

```text
Migrations.Postgres/2026_01_15_001_AddUserProfileSummary.cs       # expand: new column, default null
Migrations.MongoDB/2026_01_15_001_AddUserProfilesCollection.cs    # expand: new collection
Application/UserService.cs                                        # dual-writes behind features.UseNewProfileSchema
... rollout: flag goes 1% -> 100% over a week ...
Migrations.Postgres/2026_03_01_001_DropUserProfileLegacyColumn.cs # contract: drop after flag full-on
```

Notice that:

- Each step is independently runnable. A partial failure leaves the system in a state the next deploy can recover from.
- The application owns the coordination. The migration system never has to know about the cross-store dependency.
- The order of runner invocation in `Program.cs` does not matter for correctness, only for deploy speed.

### What not to do

Do not write a migration that assumes both stores succeed atomically:

```csharp
// DON'T: this assumes Postgres DDL and Mongo insert succeed together.
[Migration(1000)]
public class AddUserProfile : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        await _postgres.ExecuteAsync( "ALTER TABLE users ADD COLUMN profile_id text" );
        await _mongo.GetCollection<BsonDocument>( "user_profiles" )
            .InsertOneAsync( new BsonDocument( "_id", "default" ), ct );
    }
}
```

If the Postgres `ALTER TABLE` succeeds and the Mongo insert fails, the system is in a half-applied state with no rollback path -- squashes are up-only ([ADR-0020](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0020-squashes-are-up-only.md)) and the Postgres migration's ledger entry is already committed. Recovery requires manual intervention.

Express the change as separate per-provider migrations and put the coordination in application code.

## Notes and gotchas

- **Ordering between runners is your choice.** The library does not impose an order. Pick a deterministic sequence for your host (alphabetical, or "schema first then data," or whatever your team agrees on) and document it.
- **Profiles are per-provider.** `OpenSearchMigrationOptions.Profiles` and `AerospikeMigrationOptions.Profiles` are independent. A migration class belongs to exactly one provider's discovery scope -- migrations cannot span providers.
- **Discovery assemblies are per-provider.** Each provider's `Assemblies` setting controls which assemblies it scans. Two providers pointing at the same assembly is legal but unusual.
- **`services.Replace` semantics.** In a multi-provider host, `services.Replace(ServiceDescriptor.Singleton<MigrationRunner>(...))` replaces only the (now-throwing) base alias, not the per-provider subclasses. To wrap or override a runner, replace the typed subclass: `services.Replace(ServiceDescriptor.Singleton<PostgresMigrationRunner>(...))`.
- **The CLI runners stay per-provider per binary.** `Hyperbee.MigrationRunner.Postgres` and `Hyperbee.MigrationRunner.Aerospike` are separate executables. A multi-provider in-process host does not change how the standalone runners are deployed.
- **Squash is per-provider.** Each runner squashes its own ledger; the squash CLI is invoked separately per provider with `--provider <name>` and its own fleet manifest.

## Why the library does not ship a multi-runner coordinator

A "thin coordinator" that runs N runners in declared order and reports aggregate status looks attractive but creates the wrong affordance: operators treat the aggregate status as a transactional outcome and stop coding the application-layer expand/contract. The migration system has the wrong primitives for cross-store coordination -- no application context, no feature flags, no compensating transactions.

The act of writing the foreach loop you see above is intentional. It forces you to confront the failure semantics for your specific application instead of inheriting a one-size-fits-all policy.

For the architectural rationale see [ADR-0023: Multi-Runner Composition (Not a Cross-Provider Meta-Runner)](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0023-multi-runner-not-meta-runner.md).
