# Hyperbee Migrations OpenSearch Provider

OpenSearch provider for Hyperbee Migrations. Migrations are written as resource files (`statements.json`) and executed against a live cluster using a Parlot-parsed statement grammar.

## Status

Under active development on `devs/bfarmer/provider-opensearch`.

- `docs/requirements/opensearch-provider.md` — 31 testable requirements
- `docs/design/opensearch-provider.md` — Pragmatic Hybrid architecture
- `docs/decisions/0011-0015` — provider-specific ADRs
- `docs/plans/active/opensearch-provider.md` — implementation plan

## Features

- Migration tracking via dedicated ledger index with strict mapping and forensic fields
- Auto-renewing distributed lock with realtime-GET takeover and bounded lifetime
- Resource-driven migrations: Parlot-parsed statement execution
- Composite `MIGRATE INDEX` verb encoding the canonical zero-downtime reindex-and-swap pattern (R-30)
- Atomic `ALIAS SWAP` with in-body precondition (no TOCTOU window)
- Component templates, ISM policies, conditional execution
- Hybrid parser+runtime injection for safe defaults (`op_type: create`, `dynamic: strict`)
- Per-statement opt-in rollback with partial-rollback ledger semantics (R-19)
- Single-node dev, multi-node prod; AWS Managed OpenSearch (SigV4 in a follow-up slice)

---

## Quick start

```csharp
services.AddOpenSearchMigrations( opts =>
{
    opts.LedgerIndex = ".migrations";
    opts.LockingEnabled = true;
} );
```

```json
// Resources/1000-CreateInitialIndex/statements.json
{
  "statements": [
    {
      "statement": "CREATE INDEX users IF NOT EXISTS WITH BODY $usersIndex",
      "usersIndex": {
        "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
        "mappings": {
          "properties": {
            "id":    { "type": "keyword" },
            "email": { "type": "keyword" },
            "name":  { "type": "text" }
          }
        }
      }
    }
  ]
}
```

```csharp
[Migration( 1000 )]
public class CreateInitialIndex( OpenSearchResourceRunner<CreateInitialIndex> runner ) : Migration
{
    public override Task UpAsync( CancellationToken ct = default )
        => runner.StatementsFromAsync( "statements.json", ct );
}
```

The companion runner project (`runners/Hyperbee.MigrationRunner.OpenSearch`) is the preferred deployment shape; the standalone samples in `runners/samples/Hyperbee.Migrations.OpenSearch.Samples` cover every verb below.

---

## Statement syntax

The statement grammar is a small SQL-flavored DSL. Each statement is one line; one or more statements live inside a `statements.json` resource. Statements are case-insensitive for keywords. Identifiers may be plain (`users`, `users-v1`, `users.archive`) or backtick-quoted (`` `users.v2` ``) for names containing characters the plain-form parser doesn't accept.

The grammar is **offline-pure** — no network I/O at parse time (ADR-0015). Anything that needs the live cluster (template resolution, version checks) happens at dispatch time.

### Statement summary

| Verb | Form |
|------|------|
| Index lifecycle | `CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body]` |
|                 | `DROP INDEX <name> [IF EXISTS]` |
|                 | `UPDATE MAPPING ON <idx> [WITH BODY $body]` |
|                 | `UPDATE SETTINGS ON <idx> [CLOSE] [WITH BODY $body]` |
|                 | `REFRESH <name>` |
| Alias | `ALIAS SWAP <alias> FROM <old> TO <new>` |
|       | `ALIAS ADD <alias> ON <idx>` |
|       | `ALIAS REMOVE <alias> ON <idx>` |
| Reindex | `REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body]` |
| Composite | `MIGRATE INDEX <old> TO <new> [WITH TEMPLATE <id> \| WITH BODY $body] [VIA ALIAS <alias>] [TIMEOUT <duration>]` |
| Templates | `CREATE TEMPLATE <name> [WITH BODY $body]` |
|           | `CREATE COMPONENT <name> [WITH BODY $body]` |
|           | `DROP TEMPLATE <name> [IF EXISTS]` |
|           | `DROP COMPONENT <name> [IF EXISTS]` |
| ISM | `CREATE POLICY <id> [WITH BODY $body]` |
|     | `APPLY POLICY <id> TO <pattern>` |
| Cluster waits | `WAIT FOR <green\|yellow> [ON <idx>] [TIMEOUT <duration>]` |
|               | `WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]` |
| Conditional | `WHEN VERSION <op> '<version>' <statement>` |

Durations: `<integer><s\|m\|h>` (e.g., `30s`, `5m`, `2h`). Pure integers are rejected — explicit suffix required.

### Body references

`WITH BODY $name` resolves `$name` against a sibling JSON property on the **same** statement object (R-09). The resolved value is sent verbatim as the request body — no escape-as-string nesting, full IDE JSON validation. Missing references fail at execute time with the file/index/name in the error.

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "usersIndex": { "settings": {...}, "mappings": {...} }
}
```

### Index lifecycle

#### CREATE INDEX

```
CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body]
```

The provider auto-injects `mappings.dynamic: "strict"` into the body unless (a) the body explicitly sets `mappings.dynamic`, or (b) the body uses `composed_of` (component composition) — strict is then expected to be declared at the component level. This is the R-17 safe-default rule. Authors who explicitly want a non-strict dynamic policy set it themselves; user-explicit always wins.

#### DROP INDEX

```
DROP INDEX <name> [IF EXISTS]
```

`IF EXISTS` makes drop idempotent via a HEAD probe before delete.

#### UPDATE MAPPING

```
UPDATE MAPPING ON <idx> [WITH BODY $body]
```

Sends a `PUT /<idx>/_mapping`. Note that mapping updates do **not** propagate to existing documents — for that you need a reindex (or `MIGRATE INDEX`).

#### UPDATE SETTINGS [CLOSE]

```
UPDATE SETTINGS ON <idx> [CLOSE] [WITH BODY $body]
```

Without `CLOSE`, applies dynamic settings only. `CLOSE` opts into the close → update → open dance for static settings (write-unavailable for the close window). The reopen runs in a `finally` so a settings failure still attempts to reopen the index.

#### REFRESH

```
REFRESH <name>
```

Force-refresh; useful before a follow-up read or count.

### Alias

#### ALIAS SWAP — atomic in-body precondition (R-16)

```
ALIAS SWAP <alias> FROM <old> TO <new>
```

Compiles to a single `POST /_aliases` with both `remove` (with `must_exist: true`) and `add` actions. Either both succeed or both fail; the alias never resolves to both indices simultaneously. **No separate precondition GET — TOCTOU window eliminated by the cluster's atomic body rejection.**

#### ALIAS ADD / REMOVE

```
ALIAS ADD <alias> ON <idx>
ALIAS REMOVE <alias> ON <idx>
```

Single-action `_aliases` post. Use these for initial alias setup; use `ALIAS SWAP` for the cutover.

### REINDEX

```
REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body]
```

By default the provider injects `op_type: create` into the body so a retried reindex doesn't silently overwrite documents that succeeded on the first run (R-08a). Authors who genuinely need overwrite semantics opt out via `UNSAFE("<non-empty justification>")`. Bare `UNSAFE` (no parentheses, no string) fails at parse time. Justification strings are a high-signal grep target for PR review and audit.

### MIGRATE INDEX (composite, R-30) — featured

```
MIGRATE INDEX <old> TO <new>
  [WITH TEMPLATE <id> | WITH BODY $body]
  [VIA ALIAS <alias>]
  [TIMEOUT <duration>]
```

**The canonical answer** to "how do I propagate a template/mapping change to existing data?" Decomposes at parse time into:

1. `CREATE INDEX <new>` — body resolved either from `WITH TEMPLATE <id>` (runtime `GET /_index_template/<id>`) or `WITH BODY $body` (sibling reference). Mutually exclusive.
2. `REINDEX FROM <old> TO <new>` with `op_type: create` auto-injected.
3. `ALIAS SWAP <alias> FROM <old> TO <new>` (only when `VIA ALIAS` is present).

Without `VIA ALIAS`, no swap is performed — the author retains responsibility for cutover. Without `WITH TEMPLATE` or `WITH BODY`, `CREATE INDEX` runs with no body (the cluster's own template-matching may apply).

`MIGRATE INDEX a TO a` (same source and destination) is rejected at parse time. Failure of any sub-statement halts the composite and feeds R-19's partial-rollback ledger semantics.

When the resolved template references components via `composed_of`, the provider skips `dynamic: strict` injection on the resulting CREATE INDEX (the components are expected to declare their own dynamic semantics) and emits a WARN noting that component mappings are NOT propagated through this path — `CREATE INDEX` with an explicit body bypasses cluster-side template-matching.

### Templates and components

```
CREATE TEMPLATE <name> [WITH BODY $body]
CREATE COMPONENT <name> [WITH BODY $body]
DROP TEMPLATE <name> [IF EXISTS]
DROP COMPONENT <name> [IF EXISTS]
```

Composable index templates (`PUT /_index_template/<name>`) and component templates (`PUT /_component_template/<name>`). The `IF EXISTS` guard on drops uses a HEAD probe; missing names skip cleanly. Component drops fail loudly when the component is referenced by an index template (drop the referencing template first).

### ISM (Index State Management)

```
CREATE POLICY <id> [WITH BODY $body]
APPLY POLICY <id> TO <pattern>
```

`CREATE POLICY` uploads the policy to `_plugins/_ism/policies`. `APPLY POLICY` attaches it to existing indices matching the pattern via `_plugins/_ism/add` — the dispatcher inspects the response body and surfaces logical failures explicitly: HTTP 200 with `updated_indices: 0` is mapped to `Failed`, not silent OK. For future-only attachment, declare `ism_template.index_patterns` in the policy body (handled at index-creation time by the cluster).

### Cluster waits

```
WAIT FOR <green|yellow> [ON <idx>] [TIMEOUT <duration>]
WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]
```

`WAIT FOR YELLOW` is the documented "not red" idiom — there is no separate "WAIT FOR not red" verb in v1. The default health threshold is `Yellow`; `WithProductionDefaults()` flips it to `Green`. Per-statement implicit waits scope to the mutated index by default (R-12, NF-3); the wait is non-fatal — explicit `WAIT FOR` is the way to make a wait load-bearing.

`WAIT UNTIL TASK` polls `_tasks/<id>` with exponential backoff (500ms → 30s ceiling). Used by long-running operations that surface a task id (e.g., reindex async dispatch in a follow-up slice).

### WHEN VERSION (R-15a)

```
WHEN VERSION <op> '<version>' <statement>
```

Statement-level prefix that gates the wrapped child on the live cluster's reported version. Comparators: `=  !=  <  <=  >  >=`. The cluster version is fetched once per dispatcher (cached) and compared **semantically** — `'2.9' < '2.10'` is true (lexical comparison would invert it). Skipped statements log the actual cluster version so ops can distinguish "cluster older than expected" from "predicate is wrong."

v1 supports `MAJOR.MINOR[.PATCH]` only. `-SNAPSHOT`, `-rc<N>`, and AWS `OpenSearch_<x>` prefix/suffix forms are rejected at parse time with a remediation message — partial-suffix support is worse than loud rejection in production.

---

## Rollback (R-19)

Each statement entry may carry an optional `rollback` field. UpAsync runs `statement` fields in declaration order; DownAsync (via `RollbackStatementsFromAsync`) runs `rollback` fields in **reverse** declaration order — last operation applied is the first to undo.

```json
{
  "statements": [
    {
      "statement": "CREATE INDEX audit_v1 IF NOT EXISTS",
      "rollback":  "DROP INDEX audit_v1 IF EXISTS"
    },
    {
      "statement": "ALIAS ADD audit ON audit_v1",
      "rollback":  "ALIAS REMOVE audit ON audit_v1"
    }
  ]
}
```

```csharp
public override Task UpAsync( CancellationToken ct = default )
    => runner.StatementsFromAsync( "statements.json", ct );

public override Task DownAsync( CancellationToken ct = default )
    => runner.RollbackStatementsFromAsync( this, "statements.json", ct );
```

### Validation pass

Before any rollback dispatches, the runner walks the full statement list and verifies every entry has a `rollback` field. A missing rollback aborts Down with `RollbackNotSupportedException(StatementIndex)` **before** any state is mutated. This is deliberate — a half-rolled-back state is harder to recover from than no rollback at all. Operations that are genuinely irreversible (mapping changes, dropped data) belong in migrations that don't expose Down.

### Partial-rollback ledger semantics (R-19, R-24c keystone)

When rollback statement N fails after N+1..M have already rolled back, the migration's ledger entry is overwritten to `status: partially_rolled_back` with `failedStatementIndex: N`. **Subsequent runs in either direction are refused** with `OpenSearchPartialRollbackException`, which carries a remediation message — silent retry could leave the cluster in an indeterminate intermediate state.

To recover:

1. Inspect the ledger entry: `GET /.migrations/_doc/<recordId>`
2. Reconcile cluster state manually so the rollback can complete cleanly from the failing index forward.
3. Re-run with `OpenSearchMigrationOptions.ForceResume = true` (or `--force-resume` on the runner CLI).

---

## Authentication (R-21)

The provider supports four auth modes for the core package; SigV4 ships in a separate opt-in extension (plan task 3.2). Configure via `services.AddOpenSearchClient(endpoint, opts => ...)` or via `IConfiguration` under the `OpenSearch:Authentication:*` section. The runner project surfaces all four through CLI flags.

| Mode | Use when | Required fields |
|------|----------|-----------------|
| `Anonymous` | Local dev cluster with the security plugin disabled | (none — emits a startup WARN) |
| `Basic` | Standard username/password setup | `UserName` (Password may be empty) |
| `ApiKey` | OpenSearch security-plugin API keys (recommended for service-to-service) | `ApiKeyId`, `ApiKey` |
| `ClientCertificate` | mTLS — corporate compliance and zero-trust setups | `ClientCertificatePath` (PFX) **or** `ClientCertificate` (X509Certificate instance); optional `ClientCertificatePassword` |

Validation runs at client-build time so missing required fields fail at startup with the configuration key to set, not at first wire request.

```csharp
services.AddOpenSearchClient( new Uri( "https://prod-cluster.example:9200" ), auth =>
{
    auth.Mode = OpenSearchAuthenticationMode.ApiKey;
    auth.ApiKeyId = config["OpenSearch:Authentication:ApiKeyId"];
    auth.ApiKey = config["OpenSearch:Authentication:ApiKey"];
} );

services.AddOpenSearchMigrations( opts => { /* ... */ } );
```

Or from `IConfiguration` directly:

```csharp
services.AddOpenSearchClient( configuration );
```

```jsonc
{
  "OpenSearch": {
    "ConnectionString": "https://prod-cluster.example:9200",
    "Authentication": {
      "Mode": "ClientCertificate",
      "ClientCertificatePath": "/secrets/migrations.pfx",
      "ClientCertificatePassword": "(use user-secrets / env vars / vault)"
    }
  }
}
```

**Anonymous emits a startup WARN.** Production deployments should always pin a non-anonymous mode; the warning is the cheapest forcing function we can afford. Mode keyword parsing is case-insensitive (`apikey` / `ApiKey` / `APIKEY` are equivalent in config).

The runner project's `--user`/`--password` flags map onto Basic; `--api-key-id`/`--api-key` map onto ApiKey; `--client-cert`/`--client-cert-password` map onto ClientCertificate. `--auth-mode` selects explicitly when needed.

## Configuration

`AddOpenSearchMigrations(Action<OpenSearchMigrationOptions>)` registers the provider. Options:

| Option | Default | Notes |
|--------|---------|-------|
| `LedgerIndex` | `.migrations` | Strict-mapped index for migration records (R-06) |
| `LockIndex` | `.migrations-lock` | Single-shard, zero-replica (PA-2) |
| `LockName` | `migration_lock` | Document id of the singleton lock |
| `LockingEnabled` | `false` | Enable distributed locking |
| `LockRenewInterval` | 30s | Heartbeat cadence |
| `LockStaleAfter` | 60s | Takeover threshold (must be ≥ 2× renew, < max-lifetime) |
| `LockMaxLifetime` | 1h | Hard cap; in-flight migration is canceled when reached |
| `ClusterHealthThreshold` | `Yellow` | `WithProductionDefaults()` flips to `Green` |
| `WaitMode` | `PerStatement` | `PerMigration` consolidates waits (forthcoming slice) |
| `ImplicitWaitTimeout` | 30s | Per-statement wait ceiling |
| `RequireUnsafeJustification` | `false` | `WithProductionDefaults()` flips to `true` |
| `ContextResolutionPolicy` | `SkipIfUnset` | `WithProductionDefaults()` flips to `RequireExplicit` |
| `ActiveContext` | `null` | Comma-separated context tags (forthcoming slice) |
| `AssumeIndicesExist` | `false` | Skip provisioning; verify-only (ADR-0013) |
| `ForceResume` | `false` | R-19 lockout bypass; CLI `--force-resume` |

`WithProductionDefaults()` is an extension method on `IServiceCollection` that opts into production-safe defaults wholesale (Green threshold, PerMigration waits, justifications required, RequireExplicit context). Per-option settings chained after it win — the marker is a forcing function, not a lock.

## Distributed lock (R-04, R-05, NF-1)

A single lock document on `LockIndex` keyed by `LockName`. Acquisition uses `op_type=create` for atomic claim. On 409, the provider does a **realtime** GET (not a search-layer read — search lag could fool a takeover decision) to inspect the existing holder; if the document is past `LockStaleAfter` since last heartbeat, the new owner CAS-overwrites via `if_seq_no`/`if_primary_term`. The renewal loop refreshes `LastHeartbeat` at `LockRenewInterval`; CAS conflicts on renew signal that another runner has taken over and the in-flight migration is canceled cleanly. `LockMaxLifetime` caps total wall-clock hold so a hung migration cannot lock forever.

## Ledger (R-06)

Strict-mapped index with the following fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | keyword | Migration record id |
| `runOn` | date | Apply timestamp |
| `direction` | keyword | `Up` \| `Down` |
| `status` | keyword | `succeeded` \| `failed` \| `partially_rolled_back` |
| `appliedBy` | keyword | `{machineName}/{processId}` |
| `checksum` | keyword | Statement-set hash (forthcoming slice) |
| `error` | text | Failure detail |
| `failedStatementIndex` | integer | Populated on `partially_rolled_back` |

Schema is **immutable** per the Forbidden trust boundary (R-06). Field additions land in releases, not at runtime. The bootstrapper verifies the schema on startup and surfaces `OpenSearchLedgerSchemaMismatchException` with the missing fields named on mismatch.

## Bootstrapper

Runs as an ordered pipeline of `IBootstrapStep` instances (ADR-0014):

1. `RestPingStep` — REST endpoint smoke test
2. `ClusterHealthStep` — cluster readiness wait
3. `LedgerIndexInitStep` — create or verify the ledger schema
4. `LockIndexInitStep` — create or verify the lock index

Failure surfaces as `OpenSearchNotReadyException` with the failed step name and inner exception.

---

## Production deployment

The companion runner project (`runners/Hyperbee.MigrationRunner.OpenSearch`) is the recommended deployment shape — same Helm chart / Dockerfile / Octopus deploy template as the other Hyperbee runners (R-26). It exposes the standard CLI flags (`--connection`, `--user`, `--password`, `--ledger`, `--lock`, `--lock-name`, `--profile`, `--file`, `--assembly`) plus `--force-resume` for R-19 recovery. See `runners/Hyperbee.MigrationRunner.OpenSearch/README.md` for the runbook.

For library use, the migration class consumes `OpenSearchResourceRunner<TMigration>` via DI and the resource-loading conventions follow the existing per-provider pattern (the `[assembly: ResourceLocation(...)]` attribute identifies the resource root).

## Forbidden behavior (trust boundary)

The provider will not:

- Run migrations without acquiring the lock (when locking is enabled)
- Bypass parse-time unsafe-operation detection silently
- Auto-generate inverse operations (rollback is opt-in only)
- Modify the migration ledger index mapping after creation
- Take over a lock based on search-staleness alone (must verify via realtime GET)
- Execute a `REINDEX` without `op_type: create` unless `UNSAFE("...")` is explicit
- Inject `dynamic: strict` into a body with `composed_of`
- Run two `MigrationRunner.RunAsync` calls concurrently within a single process
