# ADR-0024: Migration Host Discovery via Single Interface

**Status:** Accepted
**Date:** 2026-05-12 (promoted 2026-05-13)
**Amends:** None directly. Replaces an implicit Postgres-only convention (`static ApplyToDataSourceAsync(NpgsqlDataSource, ...)`) that the v1 squash CLI used for migration apply.
**Related ADRs:** ADR-0006 (Options Inheritance with DI Registration), ADR-0019 (Squash via Replaces Graph), ADR-0023 (Multi-Runner Composition).
**Assessed in:** [docs/research/0009-v3-release-readiness-assessment.md](../research/0009-v3-release-readiness-assessment.md) — R-17 + the Path A architecture decision.

## Context

The v3.0 squash CLI must execute user migrations against an ephemeral container (snapshot capture round) and against a fresh apply target (verification round). The v1 CLI (Postgres only) used reflection over a hardcoded static method signature on the migration assembly:

```csharp
public static Task ApplyToDataSourceAsync(
    NpgsqlDataSource dataSource, long fromVersion, long toVersion, CancellationToken ct);
```

This worked for one provider but does not survive v3.0's "all 5 providers" commitment:

- **Provider-specific signature:** `NpgsqlDataSource` is Postgres-only. Aerospike needs `IAsyncClient`, MongoDB needs `IMongoClient`, etc. Five different static signatures means five different reflection sites in the CLI.
- **Brittle string match:** a typo in the method name surfaces only at runtime via `NotSupportedException`. No compile-time check.
- **DI duplication:** the user's migration project already wires `services.Add{Provider}Migrations(...)` in their host application. The static method redoes that setup, drifting over time.
- **No third-party path:** a future Cassandra provider author would need to publish their own CLI fork that knows about a Cassandra-specific static signature.

The Path A audit (research/0009) requires a single contract that works for all 5 first-party providers AND any future third-party provider, with discovery a single-shot reflection at CLI startup.

## Decision

A single interface in the `Hyperbee.Migrations` core package serves as the migration apply entry point for the CLI:

```csharp
public interface IMigrationHost
{
    Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context,
        CancellationToken cancellationToken);
}

public sealed record MigrationHostContext
{
    public required string ConnectionString { get; init; }
    public Action<MigrationOptions>? OverrideOptions { get; init; }
    public IReadOnlyDictionary<string, string>? ProviderHints { get; init; }
}
```

Each migration assembly that the CLI operates on must contain **exactly one** public, non-abstract, default-constructible type implementing `IMigrationHost`. The implementer wires the user's existing `Add{Provider}Migrations` setup using the context's `ConnectionString` and any `OverrideOptions`.

The CLI performs **a single reflection step at startup** — `migrationAssembly.GetTypes().Where(t => typeof(IMigrationHost).IsAssignableFrom(t))` — and refuses with an actionable error if zero or multiple implementations are found. After discovery, every interaction with user code goes through the interface and the typed services it resolves.

The CLI assembly itself references no provider packages. The migration assembly's reference closure determines which `Add{Provider}Migrations` extensions are reachable, which determines which providers the host can configure.

## Alternatives Considered

### A1 — Per-provider static method (status quo for Postgres)

```csharp
public static Task ApplyToDataSourceAsync(NpgsqlDataSource, long, long, CancellationToken);
public static Task ApplyToMongoAsync(IMongoClient, long, long, CancellationToken);
// ...
```

**Rejected.** Each provider needs a different reflection site keyed on a different SDK type. The CLI would need to know which static method to look for based on `--provider`. Brittle string-match plus N reflection paths. Duplicates the DI configuration that already lives in the user's app host.

### A2 — Assembly attribute pointing at an entry type

```csharp
[assembly: MigrationApplyEntryPoint(typeof(MyMigrationsApply))]
```

**Rejected.** Still requires reflection (scan for the attribute, activate the type), and the entry type's method signature has to be something — either the same per-provider problem (A1) or a single interface (the decided option). The interface alone is simpler.

### A3 — Build-time source generator emitting a known-named entry type

**Rejected for v3.0; revisit in v3.x or v4 if needed.** A source generator could eliminate reflection entirely (CLI calls `Type.GetType("MyAssembly.GeneratedMigrationEntry")`). Trade-offs: adds build-time pipeline complexity, debugging hostility (generated code), and a hard dependency on Roslyn version compatibility for v2 -> v3 upgrade. The benefit (zero reflection) is small — the discovery scan is single-shot at CLI startup, not on a hot path.

### A4 — Per-project manifest file (`hyperbee-migrations.json`)

**Rejected.** Adds drift surface (manifest gets out of sync with code; one more thing to update on rename/refactor). Hand-authored config files where reflection would suffice is anti-ergonomic.

### A5 — Source-of-truth in `MigrationOptions` itself

Have the migration project register the host via `services.AddSingleton<IMigrationHost>(...)`. CLI builds an `IServiceProvider` from the migration assembly's bootstrap.

**Rejected as primary.** Composes recursively (the host produces the SP, but the host registration also needs an SP). The standalone interface is simpler to discover and document.

## Consequences

### Positive

- **One contract, all providers.** Future third-party providers (Cassandra, DynamoDB, etc.) consume the same surface as the 5 first-party providers. The CLI does not know which providers exist.
- **Single reflection site.** One discovery scan at CLI startup. No string-match brittleness; the interface is the type contract.
- **Reuses existing DI wiring.** The user already writes `services.Add{Provider}Migrations(...)`; the host class moves that wiring behind a single method that any tool (CLI, runner project, future tools) can call.
- **Multi-provider hosts work for free.** A host that wires both `AddPostgresMigrations` and `AddMongoDBMigrations` returns a service provider with both typed runners resolvable. The CLI dispatches via `--provider`.
- **Recovery + readiness probes reuse the host.** The `recover from-mid-range` verb and per-provider `FleetReadinessCheck` both build their service providers via the same host — no duplicate DI setup logic.
- **Backward compat path for v2 migration projects:** the v1 Postgres `ApplyToDataSourceAsync` static-method discovery is removed in v3.0; v2 projects upgrade by adding a 10-line `IMigrationHost` class.

### Negative

- **One small reflection step remains.** Cannot be eliminated without a source generator (A3). Acceptable trade-off — single-shot at CLI startup, not hot-path.
- **One new public type to learn.** Mitigated by per-provider sample updates and a new `docs/site/cli.md` page documenting the pattern.
- **Migration projects must add a class.** The boilerplate is ~10-15 lines per provider (or per multi-provider host). Worth the clean abstraction.
- **Single-implementer constraint.** Each migration assembly must expose exactly one `IMigrationHost`. Multiple implementations -> the CLI refuses with `InvalidOperationException` naming all candidates. Strict but unambiguous.

### Migration path from v1 Postgres

v1 (legacy):

```csharp
public static class MigrationApply
{
    public static async Task ApplyToDataSourceAsync(
        NpgsqlDataSource ds, long from, long to, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ds);
        services.AddPostgresMigrations(opts => opts.UpToVersion = to);
        await services.BuildServiceProvider()
            .GetRequiredService<MigrationRunner>()
            .RunAsync(ct);
    }
}
```

v3.0 (new):

```csharp
public class BillingMigrationsHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext ctx, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddNpgsqlDataSource(ctx.ConnectionString);
        services.AddPostgresMigrations(opts =>
        {
            opts.Assemblies = [typeof(BillingMigrationsHost).Assembly];
            opts.SchemaName = "billing";
            ctx.OverrideOptions?.Invoke(opts);
        });
        return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
    }
}
```

Migration project authors who used the legacy pattern delete the static method and add the host class. CHANGELOG documents the change as a breaking removal of the legacy convention (covered under v3.0's other v2 -> v3 breaking-with-back-compat-paths section).

## Compliance

- **ADR-0006** (Options Inheritance) — unaffected. The host wires `Add{Provider}Migrations` which produces the per-provider options subclass; nothing in this ADR changes options shape.
- **ADR-0019** (Squash via Replaces Graph) — `ISquashStrategy` capture/verifier delegates remain unchanged. `IMigrationHost` is upstream: the squash CLI uses the host to build a service provider and resolve the `{Provider}MigrationRunner`; the runner then drives the existing strategy machinery.
- **ADR-0023** (Multi-Runner Composition) — composes cleanly. A host that registers multiple providers produces a service provider where each `{Provider}MigrationRunner` resolves independently; the squash CLI's `--provider` flag selects which typed runner to invoke. The base `MigrationRunner` resolution still throws in multi-provider hosts.

## Decisions

- **2026-05-12** Adopted `IMigrationHost` as the single migration apply entry point. Replaces the v1 Postgres static-method convention.
- **2026-05-12** Discovery via single reflection scan at CLI startup. Multiple implementers in one assembly is an error.
- **2026-05-12** Migration assembly's reference closure determines available providers. CLI references zero provider packages.
- **2026-05-12** Source-generator alternative (A3) deferred — single-shot reflection is acceptable for v3.0.
- **2026-05-12** No backward-compat shim for the v1 Postgres `ApplyToDataSourceAsync` static method — v3.0 is a major release; the upgrade guide describes the host-class replacement pattern.

## Test plan

- **Unit:** `MigrationHostDiscoveryTests` covers zero-implementations / one-implementation / multiple-implementations / non-default-ctor / abstract-implementations / non-public cases. The interface itself + context record have property-init tests.
- **Integration:** each provider sample in `runners/samples/Hyperbee.Migrations.*.Samples/` adds a `*MigrationsHost.cs` and demonstrates `dotnet hyperbee-migrations squash --assembly <sample.dll>` invoking the host. Sample integration tests exercise the host being discovered, configured, and used to drive a squash codegen round-trip.

## Amendments

### A1 (2026-05-13) — `IEphemeralProvisioner` extension

`{Provider}SquashProvider.GenerateAsync` originally bound its container provisioning inline (each provider's snapshot capture orchestrator constructed its `Testcontainers.*Container` directly inside the capture method). A1 lifts that into a small abstraction so the lifecycle is replaceable:

```csharp
public interface IEphemeralProvisioner
{
    Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken);
}

public interface IEphemeralFixture : IAsyncDisposable
{
    string ConnectionString { get; }
    IReadOnlyDictionary<string, string> Metadata { get; }
}
```

Each per-provider Squash package ships:
- A default Testcontainers-backed provisioner (`{Provider}EphemeralProvisioner`) consumed by `{Provider}SquashProvider`'s default constructor.
- A typed fixture (`{Provider}EphemeralFixture : IEphemeralFixture`) carrying any provider-specific handles (e.g., `PostgreSqlContainer` for the `pg_dump --schema-only` exec path) alongside the connection string.
- A second `{Provider}SquashProvider(IEphemeralProvisioner)` constructor so integration tests and third-party embeddings can substitute their own provisioner.

Couchbase additionally ships `CouchbaseSiblingContainerProvisioner` — when the CLI itself runs inside a Docker container, the sibling pattern provisions Couchbase as a sibling container on the same Docker network rather than a child Testcontainers instance. The provisioner returns the fixture with `network-alias` metadata so the snapshot capture client can route via the alias.

**Why:** v3.0's host-side Couchbase F-1 issue surfaced the need for an explicit lifecycle hook. Inline `new Testcontainers.*Container()` in the capture method tied us to one provisioning shape forever; with the abstraction, the sibling-container variant becomes a drop-in alternative without rewriting the strategy code path.

**Compliance:** ADR-0019 (Squash via Replaces Graph) unaffected — the strategy contract still consumes a capture function; only the orchestrator that builds that function gained an injection point. ADR-0023 (Multi-Runner) unaffected.

### A2 (2026-05-13) — Plugin-style `AssemblyLoadContext` isolation

`MigrationAssemblyLoader` originally used a collectible `AssemblyLoadContext` whose `Load` override probed only the migration assembly's directory. A2 extends this to a full plugin-style loader:

1. **Shared-type identity for the host/plugin boundary.** When the migration assembly references an infrastructure type that the CLI binary also references (e.g., `Microsoft.Extensions.DependencyInjection.Abstractions.IServiceCollection`), the load context must return the **same type identity** as the CLI binary holds. The loader does this by:
   - First checking the Default ALC's already-loaded assemblies by name.
   - Then attempting `AssemblyLoadContext.Default.LoadFromAssemblyName(name)` — this routes through the CLI binary's own deps.json + shared framework probing.
   - Only on Default-ALC failure does the loader fall back to migration-side resolution.

   Without this discipline, the migration project's transitive `Microsoft.Extensions.DependencyInjection.Abstractions.dll` loads into the collectible ALC as a second copy; the Postgres provider then calls `RegisterBaseAliases<IServiceCollection>(...)` with one type identity while the binder resolves a different identity, surfacing as `MissingMethodException` at the first cross-ALC dispatch.

2. **NuGet-cache probe via deps.json.** Library projects (migration assemblies) don't ship a `runtimeconfig.json`, so `AssemblyDependencyResolver` returns null for NuGet packages. The loader parses the migration assembly's `.deps.json` directly to find each package's NuGet path (`<name>/<version>`) and the runtime DLL path (`lib/<tfm>/<dll>`), then composes the absolute path under `~/.nuget/packages` (or `NUGET_PACKAGES` if set).

3. **Directory scan for build-side-effect references.** `SquashProviderRegistry.Discover` augments the metadata-driven reference closure with a scan of every managed DLL in the migration assembly's directory. The C# compiler strips unused references from assembly metadata, so a sample that has a `<ProjectReference>` to `Hyperbee.Migrations.Providers.*.Squash` but never *uses* a type from it drops out of the metadata closure. The directory scan catches these and loads them via the migration ALC so their `ISquashProvider` types share identity with the host's `ISquashProvider` interface.

**Why:** The v1 CLI worked because Postgres-only didn't need the Squash DLL at runtime (the static-method convention only required `NpgsqlDataSource`). The v3.0 plug-in model puts the provider's actual `ISquashProvider` implementation on the discovery path, which surfaces all three of these previously-latent load-context concerns.

**Compliance:** ADR-0019 unaffected. ADR-0023 unaffected. The host/plugin shared-type contract is unstated in this ADR's original body — A2 codifies it as a requirement of the discovery mechanism.

## Status

- Proposed: 2026-05-12.
- Implementation: Week 2 Day 1 of Path A (see `docs/research/0009-v3-release-readiness-assessment.md`).
- Accepted: 2026-05-13 — all 5 providers ship a working `IMigrationHost` host class; CLI binary E2E test (`CliBinaryEndToEndTests`) drives Postgres host through the actual `hyperbee-migrations.exe` child process and validates emitted `.sql` + `.metadata.json` + `.summary.md` artifacts; 5 per-provider SquashProvider integration tests pass on net8/net9/net10 (F-1 closed: per-provider integration matrix + `CouchbaseHelper` planner-catalog retries removed the Couchbase host-side race).
- Amendments: A1 (`IEphemeralProvisioner` extension, 2026-05-13), A2 (plugin-style ALC isolation, 2026-05-13).
