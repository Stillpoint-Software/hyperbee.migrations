# ADR-0023: Multi-Runner Composition (Not a Cross-Provider Meta-Runner)

**Status:** Accepted (promoted 2026-05-11 -- per-provider runner subclasses + RegisterBaseAliases + fail-loud multi-provider detection shipped; 374/374 core + 803/803 squash tests green)
**Date:** 2026-05-08 (original) / 2026-05-11 (promoted to Accepted)
**Amends:** ADR-0006 § DI registration shape (`AddSingleton` → `TryAddSingleton` for the base-type aliases; per-provider runner subclass registration replaces the single base-`MigrationRunner` registration).
**Related ADRs:** ADR-0003 (Provider Record Store Contract), ADR-0005 (Provider-Native Distributed Locking), ADR-0006 (Options Inheritance with DI Registration), ADR-0019 (Squash via Replaces Graph), ADR-0020 (Squashes are Up-Only)
**Assessed in:** [docs/research/0008-multi-runner-composition-assessment.md](../research/0008-multi-runner-composition-assessment.md)

## Context

A teammate observed that the existing `Add{Provider}Migrations` extension methods register `MigrationOptions`, `IMigrationRecordStore`, and `MigrationRunner` as non-keyed singletons. Calling more than one of them on the same `IServiceCollection` silently shadows earlier registrations: only the **last** provider's record store and options reach the singleton `MigrationRunner` instance, and earlier providers are dead.

This raised a wider question: what is the right architectural model when an application has more than one provider — and in particular, should one runner be able to coordinate migrations across multiple heterogeneous stores?

The two distinguishable scenarios:

1. **Multiple independent runners in one host.** App uses Postgres for transactions and MongoDB for documents, each with its own migration set on its own cadence. No cross-store coupling. **This is the dominant case.**
2. **Cross-store coordinated changes.** A single feature spans both stores ("add `users` table in Postgres AND `user_profiles` collection in Mongo as one logical change"). Either both succeed or … the failure semantics are unclear.

A scan of the migration-tool ecosystem shows nobody builds a meta-orchestrator across heterogeneous stores: Flyway, Liquibase, Atlas, EF Core, Django, Sqitch, Knex, Prisma, and Rails all configure one instance per database. The question is whether this is convention worth following or convention worth breaking.

## Decision

**Hyperbee.Migrations supports N independent runners per host, one per provider. It does not implement a cross-provider meta-runner.** Cross-store coordination is application-layer concern, not a migration concern.

The implementation shape:

1. **Per-provider runner subclass.** Each provider gets its own `{Provider}MigrationRunner : MigrationRunner` whose constructor depends on the **concrete** `{Provider}RecordStore` and `{Provider}MigrationOptions` types. The base `MigrationRunner` class is unchanged — it still owns the actual run loop. Subclasses exist solely to give DI a unique handle and to bind concrete dependencies.
2. **`Add{Provider}Migrations` registers the subclass.** Calling two `Add{Provider}Migrations` extensions on the same `IServiceCollection` produces two distinct runner registrations that do not shadow each other.
3. **Resolution by concrete type.** Callers resolve `serviceProvider.GetRequiredService<PostgresMigrationRunner>()` and `GetRequiredService<MongoDBMigrationRunner>()` and invoke them in whatever order the application requires.
4. **Backward compatibility.** When only one `Add{Provider}Migrations` is called, single-runner host code that resolves the base `MigrationRunner` still works — the base type is registered once per host via `TryAddSingleton` and binds to whichever provider was added.
5. **No keyed services required.** This works on net6+ without depending on the .NET 8 keyed-service feature.

The base `MigrationRunner.RunAsync` is unchanged. The provider-specific subclasses contribute nothing but type identity.

## Why Not a Meta-Runner

A meta-runner that coordinates migrations across multiple stores in one logical operation runs into four problems that are not accidents of any particular design:

1. **Provider lock semantics are irreducibly different.** Postgres uses session-level advisory locks; Aerospike uses UDF-based ledger locks; MongoDB uses document-level conditional writes; OpenSearch uses op_type=create with realtime GET; Couchbase uses the locks extension. Abstracting these into a single "lock" loses the safety guarantees that make each one trustworthy. A best-effort uniform lock would be silently weaker than any of the per-provider locks.
2. **Failure recovery is provider-specific.** Postgres half-applies a DDL that committed before the error → recovery is "fix state, mark applied, re-run." MongoDB lacks transactional DDL across collections → recovery is application-shaped. A meta-runner has to pick a lowest-common-denominator recovery model, and the LCD across our five providers is "give up and call an operator." Pretending otherwise courts production incidents.
3. **Atomicity across heterogeneous stores is unsolvable** without distributed transactions (which none of the five providers expose uniformly) or compensating sagas. ADR-0020 explicitly says squashes do not roll back; bolting saga semantics onto the migration system would contradict it. A "meta-runner that mostly works" is the same shape as a system that fails in the worst possible production cases.
4. **Migrations are infrastructure, not application logic.** Cross-store coordination — feature flags, dual-write, backfill, expand/contract — is application-shaped. The migration system has the wrong primitives for it (no application context, no feature flag, no observability beyond log lines). Putting cross-store orchestration in the migration system makes both layers worse.

The expand/contract pattern handles cross-store changes correctly:

```
Migration in Postgres:  add `users` table        (Postgres runner, applied)
Migration in Mongo:     add `user_profiles`      (Mongo runner, applied)
Application code:       dual-write behind a flag (the actual coordination)
Application code:       cut over once both are deployed
Migration to remove old structure (optional)
```

This places coordination at the layer that has the right primitives.

## DI Registration Sketch

The registration shape has three load-bearing properties (each addresses a flaw the assessment surfaced):

1. **All record-store types stay `internal`.** Factory-delegate registration binds the typed runner to its concrete record store via DI without exposing the type. The record store concrete contract is provider-private; the typed runner is the public surface. (Assessment F3/N1: the plan's earlier "expose for symmetry" framing was based on a verifiably wrong premise — all five providers' record stores were already internal.)
2. **Every registration uses `TryAddSingleton`.** Including the typed runner. A duplicate `Add{Provider}Migrations` call (legitimate scenario: two assemblies, an extension-on-extension pattern, a base-host helper composed with a feature-module helper) becomes a no-op rather than a DI throw. (Assessment N2.)
3. **Base-type aliases fail loud in multi-provider hosts.** The base `MigrationOptions` / `IMigrationRecordStore` / `MigrationRunner` aliases are registered conditionally: the *first* `Add{Provider}Migrations` registers them pointing at its provider; subsequent calls *replace them with throwing factories*. Single-provider hosts see the legacy resolution path unchanged; multi-provider hosts that resolve the base types get a clear `InvalidOperationException` instead of silently binding to the first-registered provider. (Assessment F1: the most important finding — without this, the fix replaces last-wins shadowing with first-wins shadowing, which is the same UX failure under a different name.)

The mechanism uses a private marker service (`MultiProviderRegistrationMarker`) that each `Add{Provider}Migrations` checks and updates. If the marker is absent, register the legacy aliases pointing at this provider. If the marker is present, the host has already registered another provider — replace the legacy aliases with throwing factories.

```csharp
public class PostgresMigrationRunner : MigrationRunner
{
    public PostgresMigrationRunner(
        PostgresRecordStore recordStore,
        PostgresMigrationOptions options,
        ILoggerFactory loggerFactory )
        : base( recordStore, options, loggerFactory ) { }
}

internal sealed class MultiProviderRegistrationMarker
{
    public string FirstProvider { get; init; } = "";
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresMigrations( this IServiceCollection services, ... )
    {
        // Provider-private types — kept internal. Factory-delegate registration
        // binds the typed runner without exposing the record store.
        services.TryAddSingleton( PostgresMigrationOptionsFactory );
        services.TryAddSingleton<PostgresRecordStore>(
            sp => new PostgresRecordStore( /* ... */ ) );
        services.TryAddSingleton<PostgresMigrationRunner>(
            sp => new PostgresMigrationRunner(
                sp.GetRequiredService<PostgresRecordStore>(),
                sp.GetRequiredService<PostgresMigrationOptions>(),
                sp.GetRequiredService<ILoggerFactory>() ) );

        services.AddTransient( typeof( PostgresResourceRunner<> ) );

        RegisterBaseAliases( services, "Postgres",
            sp => sp.GetRequiredService<PostgresMigrationOptions>(),
            sp => sp.GetRequiredService<PostgresRecordStore>(),
            sp => sp.GetRequiredService<PostgresMigrationRunner>() );

        return services;
    }

    // Common helper called by every Add{Provider}Migrations extension. Single-
    // provider hosts get the legacy alias chain pointing at their provider;
    // multi-provider hosts get throwing factories that direct callers to
    // resolve the typed runner explicitly.
    internal static void RegisterBaseAliases(
        IServiceCollection services,
        string providerName,
        Func<IServiceProvider, MigrationOptions> optionsFactory,
        Func<IServiceProvider, IMigrationRecordStore> storeFactory,
        Func<IServiceProvider, MigrationRunner> runnerFactory )
    {
        var existingMarker = services
            .FirstOrDefault( d => d.ServiceType == typeof( MultiProviderRegistrationMarker ) );

        if ( existingMarker is null )
        {
            // First provider: register the legacy aliases pointing at it.
            services.AddSingleton( new MultiProviderRegistrationMarker { FirstProvider = providerName } );
            services.AddSingleton<MigrationOptions>( optionsFactory );
            services.AddSingleton<IMigrationRecordStore>( storeFactory );
            services.AddSingleton<MigrationRunner>( runnerFactory );
        }
        else
        {
            // Second+ provider: replace the legacy aliases with throwing factories.
            // Operators using GetRequiredService<MigrationRunner>() etc. must switch
            // to GetRequiredService<{Provider}MigrationRunner>().
            ReplaceWithThrowingFactory<MigrationOptions>( services, providerName );
            ReplaceWithThrowingFactory<IMigrationRecordStore>( services, providerName );
            ReplaceWithThrowingFactory<MigrationRunner>( services, providerName );
        }
    }

    private static void ReplaceWithThrowingFactory<T>( IServiceCollection services, string newProvider )
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton<T>( _ => throw new InvalidOperationException(
            $"Multiple providers registered (this call adds {newProvider}). " +
            $"Resolve the typed runner explicitly: GetRequiredService<{{Provider}}MigrationRunner>()." ) );
    }
}
```

A multi-provider host:

```csharp
builder.Services
    .AddPostgresMigrations( ... )
    .AddMongoDBMigrations( ... );

// In startup:
var pg = sp.GetRequiredService<PostgresMigrationRunner>();
var mg = sp.GetRequiredService<MongoDBMigrationRunner>();
await pg.RunAsync( ct );
await mg.RunAsync( ct );

// This now throws with a clear message:
// var x = sp.GetRequiredService<MigrationRunner>();
//   → "Multiple providers registered ... Resolve the typed runner explicitly."
```

A single-provider host (unchanged from today — the legacy alias chain still resolves):

```csharp
builder.Services.AddPostgresMigrations( ... );
var runner = sp.GetRequiredService<MigrationRunner>();   // resolves to PostgresMigrationRunner
await runner.RunAsync( ct );
```

### Logger contract change

The base `MigrationRunner` ctor previously took `ILogger<MigrationRunner>`. With per-provider subclasses, that bound every log line to category `Hyperbee.Migrations.MigrationRunner` regardless of which provider's runner produced it — operators tailing logs cannot distinguish provider. (Assessment F7.)

The base ctor now takes `ILoggerFactory` and creates the runtime-typed logger once at construction:

```csharp
public class MigrationRunner
{
    private readonly ILogger _logger;

    public MigrationRunner( IMigrationRecordStore store, MigrationOptions options, ILoggerFactory loggerFactory )
    {
        _logger = loggerFactory.CreateLogger( GetType() );
        // ...
    }
}
```

Each subclass instance logs under its concrete type. This is a small semver event — note in CHANGELOG.

### Why keyed services were rejected

Keyed services (`AddKeyedSingleton`, `[FromKeyedServices]`) have been GA in `Microsoft.Extensions.DependencyInjection` since .NET 8. They were considered and rejected — not for stability reasons, but for ergonomics: keyed registrations require the consumer to specify the key at every resolution site (`[FromKeyedServices("postgres")]` on every parameter, or `GetRequiredKeyedService<MigrationRunner>("postgres")` at every call site). This breaks IDE auto-discovery: a developer typing `GetRequiredService<` and looking at completions doesn't see which keys are valid.

Per-provider subclasses preserve compile-time identity. `PostgresMigrationRunner` autocompletes; `GetRequiredKeyedService<MigrationRunner>("postgres")` does not.

Revisit only if a future C# language feature lets keyed services participate in compile-time generic dispatch.

## Implications

- **Squash is unchanged.** Squash is per-provider by construction; each runner handles its own ledger reconciliation. The squash CLI verb continues to target a specific provider; the fleet manifest is per-provider. (Verified during the assessment — see research/0008 § F5.)
- **Resource runners are unchanged.** `{Provider}ResourceRunner<TMigration>` is already provider-typed and doesn't conflict.
- **Documentation gains a multi-provider section** with a worked expand/contract example, a negative example showing the wrong way, and a failure-isolation code sample (try/finally per runner, `AggregateException` collection pattern, ledger inspection on partial failure). The package itself ships **no coordinator type** — the act of writing the foreach loop forces operators to confront failure semantics. (Assessment F2 reversal; F4 expansion.)
- **CLI verbs remain per-provider per binary.** Multi-provider CLI hosts are out of scope for this ADR.
- **Migration class authoring is unchanged.** A migration class belongs to one provider's runner discovery scope. Cross-provider migrations are not expressible — and intentionally so.
- **services.Replace semantics in multi-provider mode** replace only the base alias, not the per-provider subclasses. Document in the operator guide. (Assessment N4.)

## Out of Scope

- Cross-provider transaction or saga semantics.
- A meta-runner that orchestrates multiple providers in one logical operation.
- A "thin coordinator" type that runs N runners and reports aggregate status — would create the same affordance the meta-runner does, with a smaller surface. Doc-only sample instead. (Assessment F2.)
- Keyed-service variants of the registration (the per-provider subclass approach makes this redundant).
- Changes to existing single-provider host code (must remain working).

## Resolved Questions

- **Lock interaction in multi-provider hosts.** If two runners run in parallel, each acquires its own provider-specific lock — no contention. Per-provider locking serializes correctly across pods because each provider's lock is independent. Confirmed safe.
- **Profile filtering across runners.** `MigrationOptions.Profiles` is per-provider already; the multi-provider host configures each runner's profiles independently. Confirmed.
- **Discovery scope across runners.** Each provider's `{Provider}MigrationOptions.Assemblies` is independent; runners only discover migrations whose attributes match. Confirmed.
- **Sum-of-bootstraps host startup.** With Couchbase or OpenSearch in the set, sequential bootstrap is 15-60s for N=5. Operator guide documents the cost and shows a parallel-composition example for disjoint providers (safe per ADR-0005). No package helper. (Assessment PA-1, F8.)
