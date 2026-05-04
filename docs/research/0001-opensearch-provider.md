# Research: OpenSearch Provider for Hyperbee.Migrations

**Date:** 2026-05-02
**Status:** Draft
**Author:** Brenton Farmer (with research agents)
**Related:** Future ADRs for OpenSearch provider design

## Purpose

Scope a new OpenSearch provider for Hyperbee.Migrations. The library currently ships providers for Aerospike, Couchbase, MongoDB, and Postgres. The user identified three concern areas requiring deep investigation before design:

1. **Resource migrations** — how OpenSearch's JSON-heavy artifacts (mappings, settings, templates, ISM policies) map to the existing `statements.json` + Parlot grammar pattern
2. **Template management** — variable substitution across environments
3. **Async/sync and warmup concerns** — Aerospike's special index-ready polling and Couchbase's complex bootstrapper as baselines for OpenSearch's cluster-health and Tasks API

This document captures the research synthesis. It does not commit to an implementation; that is the role of the follow-on `nop:propose` evaluating concrete grammar/architecture options.

---

## 1. Existing Provider Patterns (In-House Prior Art)

### 1.1 Core contract

[MigrationRunner](../../src/Hyperbee.Migrations/MigrationRunner.cs) orchestrates: `InitializeAsync` → `CreateLockAsync` (returns IDisposable) → reflection discovery → sequential `UpAsync`/`DownAsync` → journal `WriteAsync`/`DeleteAsync`.

[IMigrationRecordStore](../../src/Hyperbee.Migrations/IMigrationRecordStore.cs) defines seven methods total. [MigrationRecord](../../src/Hyperbee.Migrations/MigrationRecord.cs) is minimal: `{ Id, RunOn }`. The runner is stateless; the store holds all state.

All providers implement `IMigrationRecordStore` directly and inherit from `MigrationOptions`. ADR-0003 formalizes this contract; ADR-0006 formalizes the options hierarchy.

### 1.2 Resource migrations

The convention across NoSQL providers (ADR-0002):

```json
{ "statements": [ { "statement": "..." } ] }
```

| Provider  | Statement language          | Document loader       | Grammar |
|-----------|-----------------------------|-----------------------|---------|
| Aerospike | Subset of AQL               | `DocumentsFromAsync`  | Parlot  |
| Couchbase | Partial N1QL                | `DocumentsFromAsync`  | Parlot  |
| MongoDB   | Mongo shell-like commands   | `DocumentsFromAsync`  | Parlot  |
| Postgres  | Raw SQL files (no parsing)  | None (procedural)     | None    |

Resource discovery is via embedded assembly resources, addressed by `Migration.VersionedName<T>()` (ADR-0009).

### 1.3 Templating

**No provider currently uses templating.** Hyperbee.Templating exists in-house with `{{name}}`, `{{x => x.Foo()}}`, `{{#if}}`/`{{/if}}`, `{{each}}`/`{{while}}`, and `{{name:value}}` syntax — but no migration provider has wired it in. Substitution is currently done from typed options at runtime (e.g., `_options.Namespace` in Aerospike). OpenSearch will be the first provider to require true file-level templating because mappings, replica counts, analyzer chains, and ISM policy values vary across environments.

### 1.4 Statement grammar

Three providers use Parlot (ADR-0001) for partial DSLs. Each grammar:

- **Aerospike**: `CREATE INDEX [IF NOT EXISTS] [RECREATE] [WAIT] name ON ns.set (bin) [STRING|NUMERIC|GEO2DSPHERE]`, `DROP INDEX ns indexname`, `CREATE SET`, `INSERT INTO`, `DELETE FROM`
- **Couchbase**: `CREATE BUCKET ... TYPE ... RAMQUOTA ... FLUSH ENABLED ... REPLICAS`, `CREATE [PRIMARY] INDEX`, `CREATE SCOPE`, `CREATE COLLECTION`, `BUILD INDEX ON`, `UPDATE ... SET`, `DROP {BUCKET|SCOPE|COLLECTION}`
- **MongoDB**: `CREATE COLLECTION`, `DROP COLLECTION`, `CREATE [UNIQUE] INDEX name ON db.collection(field, ...)`, `DROP INDEX name ON db.collection`

All grammars are deliberately partial — they recognize verb prefixes; everything past that point is passed through to the database client. This is the key idea worth replicating for OpenSearch: thin shell over opaque payloads.

### 1.5 Async/sync model

All record store methods are `async Task`. Cancellation tokens thread through runner → store → resource runners. Timeouts use a custom [TimeoutTokenSource](../../src/Hyperbee.Migrations/Wait/TimeoutTokenSource.cs) + linked CTS pattern.

### 1.6 Warmup / readiness

Spectrum across the four providers:

| Provider  | Warmup style                                                                                               |
|-----------|------------------------------------------------------------------------------------------------------------|
| Postgres  | None; `InitializeAsync` creates schema + table inline                                                      |
| MongoDB   | None; just acquires the database handle                                                                    |
| Aerospike | Per-operation: `WaitForIndexReadyAsync` polls `sindex/<ns>/<idx>` info command, 500ms→5s exponential, 60s default |
| Couchbase | 7-state bootstrapper: REST ping → cluster healthy → 5s settle → `WaitUntilReadyAsync` → bucket ready → sacrificial query |

Couchbase is the most complex by a wide margin and is the closest behavioral analog for OpenSearch (multi-node cluster, eventual consistency on metadata, "ready vs healthy" distinction). [WaitHelper.WaitUntilAsync](../../src/Hyperbee.Migrations/Wait/WaitHelper.cs) + [PauseRetryStrategy](../../src/Hyperbee.Migrations/Wait/RetryStrategy.cs) (ADR-0008) are the reusable primitives.

### 1.7 Distributed locking

| Provider  | Lock pattern                                                                                  |
|-----------|-----------------------------------------------------------------------------------------------|
| Aerospike | CAS `Put` with `RecordExistsAction.CREATE_ONLY` + TTL + background `Touch` renewal loop using `TimeProvider` |
| Couchbase | `RequestMutexAsync` + `AutoRenew()` from `Couchbase.Extensions.Locks`                         |
| MongoDB   | Document with `LockedOn`/`ReleaseOn` timestamps; manual expiry check; no renewal              |
| Postgres  | Dedicated `ledger_lock` row with `release_on`; manual expiry check; no renewal                |

The Aerospike auto-renewing lock (recently shipped) is the freshest and most robust pattern. ADR-0005 documents the provider-native locking decision. The Aerospike pattern translates directly to OpenSearch via `_seq_no`/`_primary_term` CAS — there is no native lock primitive in OpenSearch, and no .NET library provides one.

### 1.8 Migration record stores

| Provider  | Storage                              | Lock storage                            |
|-----------|--------------------------------------|-----------------------------------------|
| Aerospike | Set `SchemaMigrations`, key=record id, bins `Name`/`ExecutedAt` | Same set, key `migration_lock` |
| Couchbase | Bucket `ledger`, scope `migrations`, collection `ledger`        | Same collection, doc id = lock name |
| MongoDB   | Database `migration`, collection `ledger`                       | Same collection, fixed id `1` |
| Postgres  | Schema `migration`, table `ledger`                              | Separate table `ledger_lock` |

### 1.9 DI shape

```csharp
services.AddXxxMigrations( options => {
    options.Assemblies.Add( typeof(MyMigration).Assembly );
    options.LockingEnabled = true;
} );
```

Options factory binds `IConfiguration` (`Migrations:FromAssemblies`, `Migrations:FromPaths`) merged with the lambda. `IMigrationRecordStore` and `MigrationRunner` register as singletons; resource runner is generic transient.

### 1.10 Testing

[Hyperbee.Migrations.Integration.Tests](../../tests/Hyperbee.Migrations.Integration.Tests/) uses Testcontainers per ADR-0010. Pattern: spin container, embed migrations in test assembly, run as subprocess, capture logs, assert database state. Testcontainers ships an OpenSearch image — the same pattern applies.

---

## 2. OpenSearch as a Migration Target

### 2.1 .NET clients (state of the world, 2026)

| Aspect          | OpenSearch.Net (low-level)        | OpenSearch.Client (high-level)   |
|-----------------|-----------------------------------|----------------------------------|
| Forked from     | Elasticsearch.Net                 | NEST                             |
| Role            | Transport, raw request/response   | Strongly-typed POCOs, fluent DSL |
| Version         | 1.8.0 stable                      | 1.8.0 stable                     |
| TFMs            | netstandard2.0 + net6.0           | netstandard2.0 + net4.6.1        |
| License         | Apache 2.0                        | Apache 2.0                       |
| Async           | Every method has `*Async`         | Every method has `*Async`        |

Forked from Elastic 7.10.2 in 2021. There is no v8 rewrite; `main` continues 2.0.0 development on the same surface area. API is essentially identical to NEST 7 — NEST documentation and StackOverflow knowledge transfers.

Auth: basic auth, API key, mTLS, fine-grained security plugin via the high-level client. AWS SigV4 via separate package `OpenSearch.Net.Auth.AwsSigV4`.

### 2.2 Migratable artifacts

| Artifact | API | Idempotency | Pitfall |
|---|---|---|---|
| Index | `PUT /{name}` | No (errors on exists) | Static settings frozen at create |
| Mapping update | `PUT /{idx}/_mapping` | Additive only | **Existing docs are NOT reindexed** |
| Settings update | `PUT /{idx}/_settings` | Idempotent (dynamic only) | Static settings need close→update→open |
| Composable index template | `PUT /_index_template/{name}` | Idempotent | Only matches future indices |
| Component template | `PUT /_component_template/{name}` | Idempotent | Cannot delete if referenced |
| Alias | `POST /_aliases` | Atomic across multi-action body | `is_write_index` exactly one |
| Ingest pipeline | `PUT /_ingest/pipeline/{id}` | Idempotent | Order migrations carefully |
| Stored script | `PUT /_scripts/{id}` | Idempotent | |
| ISM policy | `PUT /_plugins/_ism/policies/{id}` | Update needs `if_seq_no`/`if_primary_term` | `ism_template` only matches future indices |
| Data stream | `PUT /_data_stream/{name}` | Not idempotent | Requires backing template first |
| Reindex | `POST /_reindex?wait_for_completion=false` | Not idempotent | 30s default sync timeout — always async |
| Snapshot/restore | `_snapshot` APIs | Idempotent in name | Restore can't target an open index |
| Security objects | `/_plugins/_security/api/...` | Idempotent | Requires admin role |
| Cluster settings | `PUT /_cluster/settings` | Idempotent | Transient settings vanish on full restart |

### 2.3 Async / long-running operations

This is the section the user flagged as critical. **Most "structural" operations apply asynchronously inside the cluster — the HTTP call returns when the cluster master accepts the change, not when shards are allocated and ready.**

Operations that return before applying:
- `PUT /{idx}` — accepts `?wait_for_active_shards=N|all` and `?timeout=`
- `PUT /{idx}/_settings` — dynamic instant; static needs close+update+open
- `PUT /{idx}/_mapping` — published in cluster state; existing docs unmodified
- `POST /_reindex` — always pass `?wait_for_completion=false` for migrations
- `POST /{idx}/_forcemerge` — supports async
- `_snapshot` and restore — both default async; status via `_status` and `_recovery`
- `POST /{idx}/_close|_open` — async; triggers shard reallocation
- `POST /{idx}/_refresh` — synchronous, cheap

**The three primitives every migration must use:**

1. **Tasks API** — `?wait_for_completion=false` returns `task_id`; poll `GET /_tasks/{task_id}` until `completed: true`. Cancellation via `POST /_tasks/{task_id}/_cancel`.
2. **Cluster health** — `GET /_cluster/health?wait_for_status=yellow|green&wait_for_no_relocating_shards&timeout=` is the canonical "ready" gate. Single-node clusters can never reach green when `number_of_replicas >= 1`; threshold must be configurable.
3. **Optimistic concurrency** — `_seq_no` + `_primary_term` for the migration ledger and lock document. 409 `version_conflict_engine_exception` is the signal another runner won.

### 2.4 Warmup and consistency concerns

Direct mapping of Hyperbee.Migrations' existing concerns:

| Concern (existing provider)                       | OpenSearch analog                                                                |
|---------------------------------------------------|----------------------------------------------------------------------------------|
| Aerospike: wait for index ready                   | Wait for cluster health + active shards after `PUT /{idx}`                       |
| Couchbase: bucket warmup                          | Wait for cluster status `yellow` (or `green`) after structural changes           |
| Couchbase: sacrificial query post-warmup          | Optional `_refresh` on managed indices; `wait_for` on critical writes            |
| All: index visibility post-create                 | 1s default refresh interval; use `?refresh=wait_for` for read-after-write tests  |

Specific gotchas:
- Mapping changes do NOT reindex existing docs.
- Static settings (`number_of_shards`, `analysis.*`, codec) require close/open — destructive to writes.
- Aliases switching during reindex is the canonical zero-downtime pattern (atomic multi-action `_aliases` body).
- ISM policy attachment to existing indices is a separate `POST /_plugins/_ism/add` step beyond `ism_template`.

### 2.5 Existing migration tools (prior art)

| Tool | Lang | Format | State | Lock | Notable |
|---|---|---|---|---|---|
| [senacor/elasticsearch-evolution](https://github.com/senacor/elasticsearch-evolution) | Java | `.http` files | Internal index, checksum-on-replay | Lock-doc | Flyway-style; closest to "ready to use" |
| [babenkoivan/elastic-migrations](https://github.com/babenkoivan/elastic-migrations) | PHP | PHP class up/down | Laravel migration table | Laravel | Mixes ES with external state DB |
| [hubrick/elasticsearch-migration](https://github.com/hubrick/elasticsearch-migration) | Java | YAML with verb enum | Internal index | — | Closest prior art to a typed-statement DSL |
| [quandoo/elasticsearch-migration](https://github.com/quandoo/elasticsearch-migration) | Java | YAML changesets | Internal index | — | |
| [liquibase-opensearch](https://github.com/liquibase/liquibase-opensearch) | Java | Liquibase changelog with one `httpRequest` change type | Liquibase changelog table | Liquibase | Concedes abstraction; pure pass-through |
| [zobayer1/elastic-migrate](https://github.com/zobayer1/elastic-migrate) | Python | JSON config | — | — | Small CLI |
| [medcl/esm](https://github.com/medcl/esm) | Go | CLI flags | — | — | Pure data-mover, not schema-migration |

**No widely-used .NET-native ES/OpenSearch migration library exists.** Thomas Ardal's [NEST migration pattern](https://thomasardal.com/elasticsearch-migrations-with-c-and-nest/) is a 2018 blog example, not a packaged library. This OpenSearch provider would fill a real gap.

### 2.6 State / metadata index

Recommended baseline:
- One index, doc-per-migration, keyed by migration id (e.g., `1000.m1000-createindex`)
- `dynamic: strict` mapping — typo-proof
- Update with `if_seq_no`/`if_primary_term` — concurrent runners get clean 409
- Index ledger writes with `?refresh=wait_for` — ledger is tiny, cost is irrelevant

### 2.7 Distributed locking

There is **no native lock primitive** in OpenSearch and **no .NET library** implements one. (The Java OpenDistro `LockService` is internal, used by ISM, not a public client API.) Practical options:

1. **Lock-doc with explicit heartbeat** — owner periodically updates `last_heartbeat` with `if_seq_no`. Takeover requires staleness check + CAS overwrite. Mirrors Aerospike auto-renewing pattern.
2. **Lock-doc with TTL via ISM** — ISM policy deletes docs older than N minutes. Same renewal-vs-TTL race as Aerospike.
3. **External lock (Redis/etcd/ZooKeeper)** — clean semantically; biggest dependency cost.

Option 1 (heartbeat CAS) is the recommendation. Aerospike's `LockHandle` design is directly portable.

### 2.8 Resource file conventions

Three live patterns in the wider ecosystem:
- Raw HTTP method + path + JSON body (elasticsearch-evolution)
- Typed verbs over JSON bodies (hubrick) ← closest to in-house Couchbase pattern
- Pure C# fluent (Mongock-style)

Templated mappings with `{{var}}` substitution are mandatory for any real-world tool — index names, replica counts, and analyzer chains differ across environments.

---

## 3. Statement Grammar Considerations

### 3.1 Granularity

The Couchbase pattern (one DSL statement per logical operation; multiple statements per migration class) is sound prior art:
- One DSL block per migration would force authors to invent intra-block sequencing
- One statement per migration would force class proliferation
- The `statements[]` array is the unit; each element is one verb invocation

### 3.2 JSON embedding

OpenSearch payloads are large and almost always JSON. Strategies:

| Strategy | Used by | Pros | Cons |
|---|---|---|---|
| Inline string in `"body"` | liquibase-opensearch | Simple, one file | Quote-escaping hell |
| Heredoc/folded YAML | Liquibase YAML | Readable | YAML quirks |
| `.http` file with blank-line body | elasticsearch-evolution | Best readability | Custom file format |
| External `bodyFile` reference | (rare) | Clean | Two-file lookup |
| **Sibling JSON object referenced by `$name`** | (proposed) | Real JSON tooling, no escaping | Slightly novel |

The proposal: keep the `statements.json` wrapper; each statement object can carry inline `body` as a sibling JSON object referenced by `WITH BODY $name`. Mirrors SQL parameters; avoids quote escaping.

```json
{
  "statement": "CREATE INDEX `users-v2` WITH BODY $usersIndex",
  "usersIndex": { "settings": { "number_of_shards": 2 }, "mappings": { ... } }
}
```

### 3.3 Templating

Wire Hyperbee.Templating (existing in-house) for the first time. Render the entire wrapper before parse. Recommended scopes:
- **env** — process env vars (`{{env.NODE_ENV}}`)
- **config** — IConfiguration values
- **runtime** — current migration name, version, timestamp, target cluster
- **secrets** — separate scope so secrets can be redacted in logs

Distinguish template-time `{{#if}}` (controls whether the statement string exists at all) from grammar-time `WHEN VERSION > '...'` (runtime check against live cluster). Both are valuable; do not conflate.

### 3.4 Verb set

| Verb | Maps to | Notes |
|---|---|---|
| `CREATE INDEX <name> [IF NOT EXISTS] WITH BODY $body` | `PUT /{name}` | Idempotency marker |
| `DROP INDEX <name> [IF EXISTS]` | `DELETE /{name}` | |
| `UPDATE MAPPING ON <idx> WITH BODY $body` | `PUT /{idx}/_mapping` | Reject unsafe changes at parse |
| `UPDATE SETTINGS ON <idx> [CLOSE] WITH BODY $body` | `PUT /{idx}/_settings` | Explicit `CLOSE` for static |
| `REINDEX FROM <src> TO <dst> [WITH BODY $body] [WAIT FOR COMPLETION true\|false]` | `POST /_reindex?wait_for_completion=false` + Tasks API poll | Always async by default |
| `ALIAS SWAP <a> FROM <old> TO <new>` | One atomic `POST /_aliases` body | Killer feature |
| `ALIAS ADD <a> ON <idx>` / `ALIAS REMOVE <a> ON <idx>` | `POST /_aliases` | |
| `CREATE TEMPLATE <name> WITH BODY $body` | `PUT /_index_template/{name}` | |
| `CREATE COMPONENT <name> WITH BODY $body` | `PUT /_component_template/{name}` | |
| `CREATE POLICY <id> WITH BODY $body` | `PUT /_plugins/_ism/policies/{id}` | |
| `APPLY POLICY <id> TO <pattern>` | `POST /_plugins/_ism/add` | |
| `WAIT FOR <green\|yellow> [ON <idx>] [TIMEOUT <dur>]` | `GET /_cluster/health?wait_for_status=...` | First-class wait |
| `WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <dur>]` | `GET /_tasks/{id}` poll | First-class wait |
| `REFRESH <name>` | `POST /{name}/_refresh` | |

### 3.5 Async/wait grammar

Two models: implicit (Cassandra cqlmigrate auto-waits for schema agreement) vs explicit (`WAIT FOR ...` is its own verb). Recommendation: **both**. Default implicit `WAIT FOR YELLOW TIMEOUT 30s` after `CREATE INDEX`/`REINDEX`/`ALIAS SWAP`/`UPDATE SETTINGS`/`APPLY POLICY`, configurable. Explicit `WAIT FOR` available for stronger guarantees or async-task waits.

### 3.6 Conditional execution

Liquibase preconditions are gold standard. Minimum useful set:
- `IF EXISTS <idx>` / `IF NOT EXISTS <idx>` — live cluster state
- `IF VERSION > '<semver>'` — cluster version
- `IF CONTEXT IN (prod, staging)` — Liquibase-style env tags
- Wrapper-level `context` array filters whole migration

### 3.7 Rollback

OpenSearch reality:
- Index creation has clean inverse (delete)
- Mapping changes are largely one-way
- Reindex reversible only if source kept
- ISM policies have inverses
- Alias swaps trivially reversible

Recommendation: optional `rollback` block per statement (Liquibase-style), documented as best-effort. Don't auto-generate rollbacks. Don't pretend mapping changes are reversible.

### 3.8 Atomicity

OpenSearch has no transactions. Don't pretend otherwise. Provider's contributions:
- The framework lock (already in core)
- Idempotency from `IF [NOT] EXISTS`
- Compensating actions via `rollback` block
- `ALIAS SWAP` compiles to one atomic multi-action `_aliases` body — closest thing to a transaction

---

## 4. Risks and Footguns

1. **Yellow-vs-green hardcoding** — single-node dev clusters can't reach green; must be per-environment configurable.
2. **Mapping changes silently no-op for existing docs** — provider should detect type/analyzer changes at parse and require explicit reindex.
3. **Static settings require close/open** — destructive; needs explicit `CLOSE` flag.
4. **Bulk back-pressure (429)** — must use `BulkAllObservable` with backoff; expose policy.
5. **Reindex from remote auth** — requires cluster-side `reindex.remote.allowlist`; produce clear error.
6. **ISM policy attachment timing** — `ism_template` only matches future indices; existing need explicit `_plugins/_ism/add`.
7. **Lock TTL vs heartbeat race** — same gotcha already solved in Aerospike; reuse the pattern.
8. **Composable templates not retroactive** — only future indices.
9. **Reindex doesn't copy aliases/templates/settings** — only docs. New index must be created first.
10. **Cluster state size** — large template counts and deep mappings make every PUT propagate slowly.
11. **Default `dynamic: true` is dangerous** — managed indices should default `dynamic: strict`.
12. **`op_type: create` on reindex** — eliminates double-write on re-runs.
13. **Anti-pattern: SQL-style WHERE clauses** — OpenSearch is not relational; don't borrow concepts that don't map.
14. **Anti-pattern: parser without escape hatch** — every typed verb must accept `WITH BODY $body` for unforeseen edge cases.
15. **Anti-pattern: comment rules that break JSON** — comments belong in the wrapper, not the payload.
16. **Anti-pattern: hidden waits without timeout** — implicit waits must always have a finite default.
17. **Anti-pattern: unversioned grammar** — embed `dsl_version` in wrapper.

---

## 5. Top Design Implications

1. **Build on OpenSearch.Client 1.8 + OpenSearch.Net 1.8.** Optional `OpenSearch.Net.Auth.AwsSigV4`. Target net8.0/net9.0 to match the rest of Hyperbee.Migrations.
2. **Ledger lives in OpenSearch itself**, in a `dynamic: strict` index. Update with `if_seq_no`/`if_primary_term`. Index with `?refresh=wait_for`.
3. **Reuse the Aerospike auto-renewing lock pattern** ported to `_seq_no`/`_primary_term` CAS. No native primitive; no community .NET library.
4. **`WAIT FOR HEALTH` and `WAIT FOR TASK` as first-class statements.** Yellow-vs-green configurable per environment.
5. **Default async for reindex/snapshot/restore/force-merge** with Tasks API polling and exponential backoff.
6. **`BulkAllObservable` with sane defaults** (5MB batches, exponential backoff on 429, 8x parallelism). Default `refresh=false`; explicit `_refresh` at end.
7. **Hybrid resource format**: thin verb grammar + opaque JSON bodies via `WITH BODY $name`. Mustache-style templating from per-environment variables file.
8. **Atomic `ALIAS SWAP` as a built-in idiom**, compiling to one `_aliases` request body.
9. **Default-strict dynamic mapping; default `op_type: create` on reindex.**
10. **Front-load detection of unsafe operations** (type changes, field removals, static settings on open indices) at parse time with clear error messages.

---

## 6. Open Questions for nop:propose

1. **Statement grammar shape**: hybrid Parlot verb grammar (Couchbase-style) vs pure JSON action objects (hubrick-style) vs raw HTTP files (elasticsearch-evolution-style)?
2. **Body embedding**: sibling JSON object referenced by `$name` vs inline string vs external file reference?
3. **Wait policy**: implicit + explicit hybrid (recommended) vs implicit-only vs explicit-only?
4. **Ledger location**: dedicated `.migrations` index vs system index pattern vs configurable?
5. **Lock implementation depth**: full auto-renewing port from Aerospike (recommended) vs simple TTL-only vs external lock dependency?
6. **Templating engine wiring**: full Hyperbee.Templating integration vs simple `${var}` substitution vs none?
7. **Bootstrapper complexity**: Couchbase-style multi-state vs simpler health-poll-only?

These will be evaluated head-to-head in the follow-on `nop:propose` design exercise.

---

## Sources

External:
- [OpenSearch .NET clients](https://docs.opensearch.org/latest/clients/dot-net/)
- [Cluster health API](https://docs.opensearch.org/latest/api-reference/cluster-api/cluster-health/)
- [Reindex API](https://docs.opensearch.org/latest/api-reference/document-apis/reindex/)
- [ISM API](https://docs.opensearch.org/latest/im-plugin/ism/api/)
- [Index aliases](https://docs.opensearch.org/latest/im-plugin/index-alias/)
- [senacor/elasticsearch-evolution](https://github.com/senacor/elasticsearch-evolution)
- [hubrick/elasticsearch-migration](https://github.com/hubrick/elasticsearch-migration)
- [liquibase/liquibase-opensearch](https://github.com/liquibase/liquibase-opensearch)
- [Flyway concepts](https://github.com/flyway/flywaydb.org/blob/gh-pages/documentation/concepts/migrations.md)
- [Liquibase changeSet](https://docs.liquibase.com/concepts/changelogs/changeset.html)
- [Mongock v5](https://docs.mongock.io/v5/migration/)
- [cqlmigrate](https://github.com/sky-uk/cqlmigrate)
- [Hyperbee.Templating](https://github.com/Stillpoint-Software/hyperbee.templating)
- [Parlot](https://github.com/sebastienros/parlot)

In-house:
- [src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs](../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs)
- [src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseBootstrapper.cs](../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseBootstrapper.cs)
- [src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs](../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs)
- [src/Hyperbee.Migrations/Wait/WaitHelper.cs](../../src/Hyperbee.Migrations/Wait/WaitHelper.cs)
- [docs/decisions/0001-parlot-for-statement-parsers.md](../decisions/0001-parlot-for-statement-parsers.md)
- [docs/decisions/0002-resource-migration-pattern.md](../decisions/0002-resource-migration-pattern.md)
- [docs/decisions/0005-provider-native-distributed-locking.md](../decisions/0005-provider-native-distributed-locking.md)
- [docs/decisions/0008-wait-retry-infrastructure.md](../decisions/0008-wait-retry-infrastructure.md)
