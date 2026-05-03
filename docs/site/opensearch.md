---
layout: default
title: OpenSearch Provider
nav_order: 11
---

# OpenSearch Provider

The `Hyperbee.Migrations.Providers.OpenSearch` package provides OpenSearch support for Hyperbee Migrations. It manages indices, mappings, settings, aliases, templates, ISM policies, and reindex orchestration through resource-based migrations using a Parlot-parsed statement grammar. AWS Managed OpenSearch Service is supported via the optional `Hyperbee.Migrations.Providers.OpenSearch.Aws` extension package. For cross-cutting concepts, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.OpenSearch
```

For AWS Managed OpenSearch (SigV4 request signing):

```shell
dotnet add package Hyperbee.Migrations.Providers.OpenSearch.Aws
```

## Configuration

Register the OpenSearch client and migration services with the DI container. The two registration paths are mutually exclusive: call `AddOpenSearchClient` for header-based auth (Basic, ApiKey, mTLS, Anonymous) OR `AddOpenSearchAwsClient` for AWS SigV4. Each guards against the other being called first.

```csharp
// Local dev, on-prem, or any non-AWS deployment
services.AddOpenSearchClient( new Uri( "http://localhost:9200" ), auth =>
{
    auth.Mode = OpenSearchAuthenticationMode.Basic;
    auth.UserName = "admin";
    auth.Password = "password";
} );

services.AddOpenSearchMigrations( options =>
{
    options.LedgerIndex = ".migrations";        // default
    options.LockIndex   = ".migrations-lock";   // default
    options.LockingEnabled = true;
} );
```

For AWS Managed OpenSearch:

```csharp
services.AddOpenSearchAwsClient( new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ), aws =>
{
    aws.Region = "us-east-1";
    aws.Service = "es";   // "aoss" for OpenSearch Serverless
} );

services.AddOpenSearchMigrations( /* migration options */ );
```

| Option | Type | Default |
|--------|------|---------|
| LedgerIndex | string | ".migrations" |
| LockIndex | string | ".migrations-lock" |
| LockName | string | "migration_lock" |
| LockingEnabled | bool | false |
| ClusterHealthThreshold | enum | Yellow (Green via WithProductionDefaults) |
| WaitMode | enum | PerStatement (PerMigration via WithProductionDefaults) |
| ImplicitWaitTimeout | TimeSpan | 30 seconds |
| LockRenewInterval | TimeSpan | 30 seconds |
| LockStaleAfter | TimeSpan | 60 seconds |
| LockMaxLifetime | TimeSpan | 1 hour |
| ContextResolutionPolicy | enum | SkipIfUnset (RequireExplicit via WithProductionDefaults) |
| ActiveContext | string | null |
| ForceResume | bool | false (R-19 partial-rollback opt-in recovery) |

`WithProductionDefaults()` flips a coherent set of options for production deployments at once: Green threshold, PerMigration waits, RequireExplicit context resolution, justification required for UNSAFE/NO WAIT bypasses.

## Statement grammar

Migrations are written as resource files. Each `statements.json` lists one or more statements parsed via Parlot:

```json
{
  "statements": [
    {
      "statement": "CREATE INDEX users IF NOT EXISTS WITH BODY $usersIndex",
      "bodies": {
        "usersIndex": {
          "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
          "mappings": { "properties": { "id": { "type": "keyword" } } }
        }
      }
    },
    { "statement": "WAIT FOR YELLOW ON users TIMEOUT 30s" }
  ]
}
```

The full grammar covers index lifecycle (CREATE / DROP / UPDATE MAPPING / UPDATE SETTINGS / REFRESH), aliases (ALIAS SWAP / ALIAS ADD / ALIAS REMOVE), reindex with auto-injected `op_type:create` safety, the composite MIGRATE INDEX verb, composable templates and components, ISM policies, cluster waits, and conditional execution via WHEN VERSION (semver-correct, R-15a). See the [provider package README](https://github.com/Stillpoint-Software/Hyperbee.Migrations/blob/main/src/Hyperbee.Migrations.Providers.OpenSearch/README.md) for the full per-verb reference.

Bodies attach to a statement via `WITH BODY <ref>`. Three forms (ADR-0017): `@path/to/file.json` for direct file references, `$name` resolved against an inline `bodies` section, or for back-compat the original sibling-property pattern.

## MIGRATE INDEX (the canonical mapping-propagation pattern)

OpenSearch is unusual: mapping changes do NOT propagate to existing documents. UPDATE MAPPING applies to documents written AFTER the update, not before. To apply a mapping change to existing data, the canonical pattern is:

1. Create a new versioned index with the new mapping.
2. Reindex from the old index to the new (with `op_type: create` so retries are safe).
3. Atomically swap an alias from the old index to the new.

The `MIGRATE INDEX` composite verb encodes that pattern as one line:

```
MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template VIA ALIAS users-current
```

The composite expands at parse time to CREATE + REINDEX + ALIAS SWAP, with the template body fetched from the live cluster at dispatch time. Author owns naming explicitly; the migration tool stays unopinionated about index versioning conventions.

If your team is hitting "I changed the mapping but the existing data isn't seeing it", `MIGRATE INDEX` is the answer.

## Locking

The provider uses a single OpenSearch document on `LockIndex` for distributed locking. Acquisition is `op_type=create` (atomic claim); on conflict, a realtime GET checks staleness before any takeover. The renewal loop refreshes the heartbeat at `LockRenewInterval`; CAS conflicts on renewal signal that another runner has taken over and the in-flight migration is canceled cleanly. `LockMaxLifetime` caps total wall-clock hold so a hung migration cannot lock forever.

The lock index uses `number_of_replicas: 0` (PA-2) so concurrent acquire under N runners doesn't stall on replica-write coupling.

## Ledger forensics

The migration ledger captures forensic fields per R-06 so post-mortems have what they need without log spelunking:

| Field | Purpose |
|-------|---------|
| id | Record id (version-name) |
| runOn | Apply timestamp |
| direction | Up / Down |
| status | succeeded / failed / partially_rolled_back |
| appliedBy | {machineName}/{processId} |
| error | Failure detail, when applicable |
| failedStatementIndex | R-19: which rollback statement halted the Down sequence |

R-19 partial-rollback semantics: when a Down sequence halts partway, the ledger entry is overwritten to `partially_rolled_back` and subsequent runs in either direction are refused unless `ForceResume = true`. The runner CLI exposes this as `--force-resume`. See the [AWS validation runbook](../runbooks/opensearch-aws-validation.md) for the recovery protocol.

## Production deployment

The companion runner project (`runners/Hyperbee.MigrationRunner.OpenSearch`) is the recommended deployment shape. Same Helm chart / Dockerfile / Octopus deploy template as the other Hyperbee runners. CLI flags: `--connection`, `--auth-mode`, `--user`, `--password`, `--api-key-id`, `--api-key`, `--client-cert`, `--client-cert-password`, `--ledger`, `--lock`, `--lock-name`, `--profile`, `--file`, `--assembly`, `--force-resume`. See [Runners](runners.md).

## Multi-topology testing

- Single-node Testcontainers (every PR) covers the grammar surface.
- 3-node multi-node Testcontainers Compose (every PR via `multi_node_tests.yml` in CI) covers the production behaviors single-node fundamentally cannot exercise: GREEN threshold, replica allocation, shard relocation under load, lock-index replicas:0 invariant.
- AWS Managed OpenSearch is validated via the [AWS validation runbook](../runbooks/opensearch-aws-validation.md), pre-release and nightly when AWS credentials are available in CI.

See `tests/Hyperbee.Migrations.Integration.Tests/Container/OpenSearch/MULTINODE.md` for how to use the multi-node harness in your own tests.

## Samples

`runners/samples/Hyperbee.Migrations.OpenSearch.Samples` ships 8 sample migrations covering every v1 verb. Sample 6 (`MigrateIndexComposite`) is featured: it is the canonical answer to "how do I propagate mapping changes to existing data?". See [Resource Migrations](resource-migrations.md).
