# Contributing to Hyperbee.Migrations

Thanks for your interest in contributing! Hyperbee.Migrations is an OSS .NET migrations
library with first-class support for Aerospike, Couchbase, MongoDB, OpenSearch, and
PostgreSQL. Contributions of all sizes are welcome - bug reports, doc fixes,
new migrations for samples, and full provider implementations.

- Documentation site: https://stillpoint-software.github.io/hyperbee.migrations/
- Issue tracker: https://github.com/Stillpoint-Software/hyperbee.migrations/issues
- Code of conduct: https://github.com/Stillpoint-Software/.github (org-wide CoC applies
  to this repo)

## How to contribute

There are three primary paths:

### Bug reports

Open a GitHub issue and include:

- Minimal repro steps (a failing test is ideal)
- Expected vs. actual behavior
- Version of `Hyperbee.Migrations` and the relevant provider package
- .NET runtime version (`dotnet --info`)
- OS and version

### Feature requests

Open an issue first to discuss the idea before writing code. Substantial features
(new public API surface, new provider, behavior changes that affect existing users)
require an Architecture Decision Record. See `docs/decisions/` for the existing ADRs
and the format we use.

### Pull requests

See "Development setup" and "Branching model" below. Small fixes (typos, doc
clarifications, obvious bugs) do not need a prior issue.

## Development setup

You need:

- .NET 8, 9, and 10 SDKs installed side-by-side. The solution multi-targets all
  three and tests run on each TFM.
- Docker engine, for Testcontainers-based integration tests.
- Optional: local installs of Aerospike, Couchbase, MongoDB, OpenSearch, or
  PostgreSQL. Testcontainers will spin these up automatically when integration
  tests run, so a local install is only useful if you want to inspect state.

## Building and testing

```
git clone https://github.com/Stillpoint-Software/hyperbee.migrations.git
cd hyperbee.migrations
dotnet restore Hyperbee.Migrations.slnx
dotnet build   Hyperbee.Migrations.slnx -c Release
dotnet test    Hyperbee.Migrations.slnx -c Release
```

Integration tests are gated behind the `INTEGRATIONS` compilation symbol. Enable
them with the `EnableIntegrationTests` MSBuild property:

```
dotnet test tests/Hyperbee.Migrations.Integration.Tests/Hyperbee.Migrations.Integration.Tests.csproj -c Release -p:EnableIntegrationTests=true
```

Multi-node OpenSearch tests are tagged `[TestCategory("LocalOnly")]` because the
GitHub-hosted runners cannot reliably sustain a 3-node cluster. Run those locally
only.

If you only have Docker images for some providers, scope the run with an
environment variable:

```
HYPERBEE_TESTS_PROVIDERS_ONLY="Postgres,MongoDb"
```

## Repo layout

| Path | Purpose |
| --- | --- |
| `src/Hyperbee.Migrations/` | Core library (runner, lock, options, migration discovery) |
| `src/Hyperbee.Migrations.Providers.*/` | Per-provider implementations |
| `runners/Hyperbee.MigrationRunner.*/` | Per-provider standalone runner executables |
| `runners/samples/` | Working samples per provider |
| `tests/` | Unit and integration tests |
| `docs/site/` | Jekyll documentation site (just-the-docs theme) |
| `docs/decisions/` | Architecture Decision Records (ADRs) |
| `docs/guides/` | Operator guides |
| `docs/plans/active/` | In-flight implementation plans |

## Branching model

We use trunk-based GitHub flow:

- Branch from `main`.
- Branch naming: `devs/<your-name>/<short-description>` for example
  `devs/jdoe/postgres-jsonb-support`.
- Open the PR against `main`. CI runs unit tests on net8, net9, and net10.
  Integration tests are local or manual.
- Squash-merge is the default.

## Coding conventions

- C# style: spaced parens, e.g. `AddPostgresMigrations( options => ... )`.
- Do not use the `global::` prefix. Resolve namespace conflicts with `using`
  aliases instead.
- No emojis in code or docs unless explicitly requested.
- Tests use MSTest, with FluentAssertions where it improves readability.
- Changes to the public API surface require an added or updated ADR in
  `docs/decisions/`.
- Run `dotnet format` before pushing. The format bot enforces this in CI and
  will push a formatting commit if you forget.

## Adding a migration to a provider's sample

1. Add a `[Migration( version )] class Foo : Migration` under
   `runners/samples/Hyperbee.Migrations.<Provider>.Samples/Migrations/`.
2. For resource-driven migrations, drop a `.sql`, `.statements.json`, or
   `.statements` file under `Resources/<version>-<Name>/`.
3. Mark each resource file as `<EmbeddedResource>` in the sample's `.csproj`.
4. Add an integration test in
   `tests/Hyperbee.Migrations.Integration.Tests/<Provider>RunnerTest.cs` that
   verifies the migration runs end-to-end.

## Adding a new provider

New providers are welcome but invasive - please open an issue first to discuss
architectural fit before starting work.

The expected shape:

- Implement `IMigrationRecordStore` for the provider (`internal` class is fine
  -- it ships through factory-delegate DI registration).
- Add a `{Provider}MigrationOptions` class. See ADR-0006 for options inheritance.
- Statement parsing uses Parlot per ADR-0001.
- Locking is provider-native per ADR-0005.
- Tests: add a unit-test parser file under `tests/Hyperbee.Migrations.Tests/`
  and an integration test class under
  `tests/Hyperbee.Migrations.Integration.Tests/`.
- Ship a sample under `runners/samples/Hyperbee.Migrations.<Provider>.Samples/`.
- Add a runner project under `runners/Hyperbee.MigrationRunner.<Provider>/`
  including a `Dockerfile`.
- Add a site doc page at `docs/site/<provider>.md` following the canonical
  shape used by existing provider pages.
- Add a package README following the canonical short shape used by existing
  provider packages. Include the standard **Multi-provider hosts** section
  pointing at the typed runner + the operator guide.

### Multi-runner DI checklist (per ADR-0023)

Every `Add{Provider}Migrations` extension must:

1. **Register the concrete options factory** with `TryAddSingleton` (idempotent
   under duplicate-registration scenarios).
2. **Register the concrete record store** via factory delegate with
   `TryAddSingleton` so the type can stay `internal`. Example:
   ```csharp
   services.TryAddSingleton<{Provider}RecordStore>( sp => new {Provider}RecordStore(
       sp.GetRequiredService<{NativeClient}>(),
       sp.GetRequiredService<{Provider}MigrationOptions>(),
       sp.GetRequiredService<ILogger<{Provider}RecordStore>>() ) );
   ```
3. **Register `{Provider}MigrationRunner`** via factory delegate with
   `TryAddSingleton`. The runner subclass takes the concrete record store +
   options + `ILoggerFactory` and forwards to the base `MigrationRunner` ctor.
4. **Wire the legacy aliases** through `services.RegisterBaseAliases(...)`
   exactly once at the end:
   ```csharp
   services.RegisterBaseAliases(
       "{Provider}",
       sp => sp.GetRequiredService<{Provider}MigrationOptions>(),
       sp => sp.GetRequiredService<{Provider}RecordStore>(),
       sp => sp.GetRequiredService<{Provider}MigrationRunner>() );
   ```
5. **Add a multi-provider integration test** under
   `tests/Hyperbee.Migrations.Integration.Tests/` that pairs the new provider
   with at least one existing provider; assert each runner's ledger is
   independent and the base `MigrationRunner` resolution throws.

The `RegisterBaseAliases` helper handles the single-vs-multi-provider behavior
for you: first provider installs the legacy aliases; subsequent providers
replace them with throwing factories that name every offending provider so
multi-provider hosts cannot silently shadow. See ADR-0023 and
[multi-provider-hosts.md](docs/site/multi-provider-hosts.md).

## Filing security issues

Do NOT open a public issue for security vulnerabilities.

If the org publishes a security policy at
https://github.com/Stillpoint-Software/.github, follow it. Otherwise email
<TODO: maintainer contact> with the details and we will respond privately
before any public disclosure.
