---
layout: default
title: OpenSearch Provider
nav_order: 12
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
    auth.Mode     = OpenSearchAuthenticationMode.Basic;
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
    aws.Region  = "us-east-1";
    aws.Service = "es";   // "aoss" for OpenSearch Serverless
} );

services.AddOpenSearchMigrations( /* migration options */ );
```

### Tuning the client

Both registration methods take an optional third argument giving you the underlying `ConnectionSettings`. It runs **last**, after the endpoint and authentication wiring, so anything you set there wins.

```csharp
services.AddOpenSearchClient(
    new Uri( "https://opensearch.internal:9200" ),
    auth => auth.Mode = OpenSearchAuthenticationMode.Basic,
    settings => settings
        .RequestTimeout( TimeSpan.FromMinutes( 2 ) )
        .MaximumRetries( 5 )
        .EnableHttpCompression() );
```

Use it for transport concerns the typed options do not model — timeouts, retries, compression, a proxy, `ServerCertificateValidationCallback` for a self-signed development cluster, `DisableDirectStreaming` while debugging — and for `DefaultMappingFor` over **your own** document types when the client is shared with application code.

You do not need this to make migrations work. The migration ledger never relies on client-level type inference, so no mapping for `OpenSearchMigrationRecord` is required.

One constraint is enforced: the ledger index is created with a `strict` mapping using camelCase field names, matching the client's default field-name inference. Replacing the serializer or setting a client-wide non-camelCase `DefaultFieldNameInferrer` would make every ledger write fail, so registration rejects it with a message naming the remediation. Scope naming changes to your own types with `DefaultMappingFor<TDocument>()`, or register a separate `IOpenSearchClient` for application use.

### Provider options

| Option | Type | Default |
|--------|------|---------|
| LedgerIndex | string | ".migrations" |
| LockIndex | string | ".migrations-lock" |
| LockName | string | "migration_lock" |
| LockingEnabled | bool | false |
| ClusterHealthThreshold | enum | Yellow |
| WaitMode | enum | PerStatement |
| RequireUnsafeJustification | bool | false |
| ContextResolutionPolicy | enum | SkipIfUnset |
| ActiveContext | string | null |
| ImplicitWaitTimeout | TimeSpan | 30 seconds |
| LockRenewInterval | TimeSpan | 30 seconds |
| LockStaleAfter | TimeSpan | 60 seconds |
| LockMaxLifetime | TimeSpan | 1 hour |
| AssumeIndicesExist | bool | false |
| ForceResume | bool | false |

### WithProductionDefaults

`WithProductionDefaults()` flips four options to production-safe values BEFORE the user's configuration callback runs, so explicit overrides still win:

| Option | Library default | Production default |
|--------|-----------------|--------------------|
| ClusterHealthThreshold | Yellow | Green |
| WaitMode | PerStatement | PerMigration |
| RequireUnsafeJustification | false | true |
| ContextResolutionPolicy | SkipIfUnset | RequireExplicit |

```csharp
services
    .WithProductionDefaults()
    .AddOpenSearchMigrations( options =>
    {
        // Per-option overrides win over the production defaults above.
        options.WaitMode = WaitMode.Off;
    } );
```

## Resource layout

A migration's resources live in a folder named after the migration class (or version). The folder ships as embedded resources in the migration project's csproj.

```
Resources/
  1000-CreateInitialIndex/
    statements.json
  3000-ComponentAndIndexTemplate/
    statements.json
    bodies/
      common-mappings-component.json
  4000-IsmPolicyAndApply/
    statements.json
    hot-warm-cold-policy.json
```

Mark each file `EmbeddedResource` in the project file:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources\1000-CreateInitialIndex\statements.json" />
  <EmbeddedResource Include="Resources\4000-IsmPolicyAndApply\statements.json" />
  <EmbeddedResource Include="Resources\4000-IsmPolicyAndApply\hot-warm-cold-policy.json" />
</ItemGroup>
```

The migration class loads its resources via `OpenSearchResourceRunner<T>`:

```csharp
[Migration( 1000 )]
public class CreateInitialIndex( OpenSearchResourceRunner<CreateInitialIndex> runner ) : Migration
{
    public override Task UpAsync( CancellationToken ct = default )
        => runner.StatementsFromAsync( "statements.json", ct );
}
```

## Statement grammar

The grammar is a small SQL-flavored DSL. Statement keywords are case-insensitive. Identifiers may be plain (`users`, `users-v1`, `users.archive`) or backtick-quoted (`` `users.v2` ``) for names containing characters the plain-form parser does not accept. The grammar is offline-pure -- no network I/O at parse time. Anything that needs the live cluster (template resolution, version checks) happens at dispatch time.

Durations use `<integer><s|m|h>` (e.g., `30s`, `5m`, `2h`). Pure integers are rejected -- the suffix is required.

### Statement file format

The runner accepts two file shapes. The script form (`.pql`) is the recommended default for new migrations (see [Resource migrations](resource-migrations.md)); the JSON-array form (`.statements.json`) is the original wrapper and is supported indefinitely. OpenSearch's script form adds two affordances over the universal shape: a `BODIES { ... }` header block that declares named bodies referenced by `$name`, and inline `WITH BODY { ... }` brace-balanced bodies that the splitter consumes as opaque blocks (semicolons inside the body are NOT statement terminators). Reversible migrations pair `<name>.pql` with a sibling `<name>.down.pql` Down script (see [Resource migrations](resource-migrations.md)).

Script form (`Resources/1000-CreateInitialIndex/statements`):

```
-- Initial sample_users index with a tight strict mapping. The BODIES header
-- declares the index body once; CREATE INDEX references it by $name. WAIT
-- FOR YELLOW gates the next statement on shard readiness.

BODIES {
  usersIndex: {
    "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
    "mappings": {
      "properties": {
        "id":     { "type": "keyword" },
        "email":  { "type": "keyword" },
        "name":   { "type": "text"    },
        "active": { "type": "boolean" }
      }
    }
  }
}

CREATE INDEX sample_users IF NOT EXISTS WITH BODY $usersIndex;

WAIT FOR YELLOW ON sample_users TIMEOUT 30s;
```

JSON-array form (`Resources/1000-CreateInitialIndex/statements.json`):

```json
{
  "bodies": {
    "usersIndex": {
      "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
      "mappings": { "properties": {
        "id":     { "type": "keyword" },
        "email":  { "type": "keyword" },
        "name":   { "type": "text"    },
        "active": { "type": "boolean" }
      } }
    }
  },
  "statements": [
    { "statement": "CREATE INDEX sample_users IF NOT EXISTS WITH BODY $usersIndex" },
    { "statement": "WAIT FOR YELLOW ON sample_users TIMEOUT 30s" }
  ]
}
```

A third body-source form (`WITH BODY @path`) loads JSON from a sibling embedded file -- see [Body references](#body-references) below.

See [Resource Migrations](resource-migrations.md) for the cross-provider details on the script form's lexical rules.

### Statement summary

| Family | Form |
|--------|------|
| Index lifecycle | `CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body] [NO WAIT("<reason>")]` |
|                 | `DROP INDEX <name> [IF EXISTS]` |
|                 | `UPDATE MAPPING ON <idx> [WITH BODY $body]` |
|                 | `UPDATE SETTINGS ON <idx> [CLOSE] [WITH BODY $body] [NO WAIT("<reason>")]` |
|                 | `REFRESH <name>` |
| Alias | `ALIAS SWAP <alias> FROM <old> TO <new> [NO WAIT("<reason>")]` |
|       | `ALIAS ADD <alias> ON <idx>` |
|       | `ALIAS REMOVE <alias> ON <idx>` |
| Reindex | `REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body] [NO WAIT("<reason>")]` |
| Composite | `MIGRATE INDEX <old> TO <new> [WITH TEMPLATE <id> | WITH BODY $body] [VIA ALIAS <alias>] [TIMEOUT <duration>]` |
| Templates | `CREATE TEMPLATE <name> [WITH BODY $body]` |
|           | `CREATE COMPONENT <name> [WITH BODY $body]` |
|           | `DROP TEMPLATE <name> [IF EXISTS]` |
|           | `DROP COMPONENT <name> [IF EXISTS]` |
| ISM | `CREATE POLICY <id> [WITH BODY $body]` |
|     | `APPLY POLICY <id> TO <pattern> [NO WAIT("<reason>")]` |
|     | `DETACH POLICY FROM INDEX <pattern> [NO WAIT("<reason>")]` |
|     | `DROP POLICY <id> [IF EXISTS]` |
| Cluster waits | `WAIT FOR <green|yellow> [ON <idx>] [TIMEOUT <duration>]` |
|               | `WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]` |
| Conditional | `WHEN VERSION <op> '<version>' <statement>` |

### Body references

JSON bodies attach to a statement via `WITH BODY <ref>`. The provider supports three resolution forms, all coexistent -- pick the one that fits the body's size and reuse profile.

#### Form 1: Direct file reference (least ceremony)

```json
{ "statement": "CREATE INDEX users WITH BODY @users-mapping.json" }
```

The `@`-prefixed path loads an embedded resource relative to the migration's own resource folder. Use this for any body that would otherwise dominate the `statements.json` file -- large mappings, ISM policies, reusable templates. Subfolders are optional. Path validation is parse-time:

- Absolute paths (leading `/` or `\`) are rejected -- body files must stay inside the migration's resource folder.
- Drive-letter prefixes (`C:`, `c:`, ...) are rejected -- same reason. `Path.IsPathRooted` is platform-dependent (`C:/foo` reads as rooted on Windows but not on Linux); the validator checks the rooted shape explicitly so an author editing on one host can't produce a path that's silently rooted on another.
- Any other `:` in the path is rejected -- embedded resource names don't use it.
- `..` segments are rejected -- no parent-directory traversal.
- Allowed characters: letters, digits, `_`, `-`, `.`, `/`, `\`.

#### Form 2: Named body inline

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "bodies": {
    "usersIndex": {
      "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
      "mappings": { "properties": { "id": { "type": "keyword" } } }
    }
  }
}
```

`$<name>` resolves to `bodies.<name>` on the same statement object. Use this for tiny bodies tightly coupled to a single statement, where atomic versioning and a single-screen view of the migration are more valuable than file separation.

#### Form 3: Named body referencing a file

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "bodies": {
    "usersIndex": "@bodies/users-mapping.json"
  }
}
```

When a `bodies.<name>` value is a string starting with `@`, the resolver loads it as a file reference (same rules as form 1). Useful when you want to address bodies by name (e.g., for clarity in PR review) but keep them in their own files.

#### Back-compat: top-level sibling property

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "usersIndex": { "settings": { } }
}
```

When `bodies.<name>` is missing, the resolver falls back to a top-level sibling property of the same name. Preserves the original body-resolution shape so existing migrations do not need rewriting.

#### Resolution order

1. `BodyFileRef` (the `@path` form): load the embedded resource.
2. `BodyRef` with a `bodies.<name>` entry: structured form wins.
3. `BodyRef` with a sibling `<name>` property: legacy fallback.
4. Otherwise: throw `OpenSearchProviderException` with a remediation message naming both the preferred form and the fallback.

## Statement reference

### CREATE INDEX

```
CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body] [NO WAIT("<reason>")]
```

Creates an index. The provider auto-injects `mappings.dynamic: "strict"` into the body unless the body explicitly sets `mappings.dynamic` or uses `composed_of` (component composition). User-explicit settings always win.

```json
{
  "statements": [
    {
      "statement": "CREATE INDEX users IF NOT EXISTS WITH BODY $usersIndex",
      "bodies": {
        "usersIndex": {
          "settings": {
            "number_of_shards":   1,
            "number_of_replicas": 0
          },
          "mappings": {
            "properties": {
              "id":    { "type": "keyword" },
              "email": { "type": "keyword" },
              "name":  { "type": "text" }
            }
          }
        }
      }
    }
  ]
}
```

### DROP INDEX

```
DROP INDEX <name> [IF EXISTS]
```

`IF EXISTS` makes drop idempotent via a HEAD probe before delete.

```json
{ "statement": "DROP INDEX users IF EXISTS" }
```

### UPDATE MAPPING

```
UPDATE MAPPING ON <idx> [WITH BODY $body]
```

Sends a `PUT /<idx>/_mapping`. Mapping updates do NOT propagate to existing documents -- for that you need a reindex (or `MIGRATE INDEX`).

```json
{
  "statement": "UPDATE MAPPING ON users WITH BODY $newFields",
  "bodies": {
    "newFields": {
      "properties": {
        "verified_at": { "type": "date" }
      }
    }
  }
}
```

### UPDATE SETTINGS

```
UPDATE SETTINGS ON <idx> [CLOSE] [WITH BODY $body] [NO WAIT("<reason>")]
```

Without `CLOSE`, applies dynamic settings only. `CLOSE` opts into the close -> update -> open dance for static settings (write-unavailable for the close window). The reopen runs in a `finally` so a settings failure still attempts to reopen the index.

Dynamic update (no close):

```json
{
  "statement": "UPDATE SETTINGS ON users WITH BODY $refresh",
  "bodies": { "refresh": { "index": { "refresh_interval": "5s" } } }
}
```

Static update with explicit CLOSE:

```json
{
  "statement": "UPDATE SETTINGS ON users CLOSE WITH BODY $analyzer",
  "bodies": {
    "analyzer": {
      "index": {
        "analysis": {
          "analyzer": { "default": { "type": "standard" } }
        }
      }
    }
  }
}
```

### REFRESH

```
REFRESH <name>
```

Force-refresh; useful before a follow-up read or count.

```json
{ "statement": "REFRESH users" }
```

### ALIAS SWAP (atomic precondition, R-16)

```
ALIAS SWAP <alias> FROM <old> TO <new> [NO WAIT("<reason>")]
```

Compiles to a single `POST /_aliases` with both `remove` (with `must_exist: true`) and `add` actions. Either both succeed or both fail; the alias never resolves to both indices simultaneously. No separate precondition GET -- TOCTOU window eliminated by the cluster's atomic body rejection.

```json
{ "statement": "ALIAS SWAP users-current FROM users-v1 TO users-v2" }
```

### ALIAS ADD / REMOVE

```
ALIAS ADD <alias> ON <idx>
ALIAS REMOVE <alias> ON <idx>
```

Single-action `_aliases` post. Use these for initial alias setup; use `ALIAS SWAP` for the cutover.

```json
{
  "statements": [
    { "statement": "ALIAS ADD users-current ON users-v1" },
    { "statement": "ALIAS ADD users-archive ON users-v0" }
  ]
}
```

### REINDEX

```
REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body] [NO WAIT("<reason>")]
```

By default the provider injects `op_type: create` into the body so a retried reindex does not silently overwrite documents that succeeded on the first run. Authors who need overwrite semantics opt out via `UNSAFE("<non-empty justification>")`. Bare `UNSAFE` (no parentheses, no string) fails at parse time.

Default-safe:

```json
{ "statement": "REINDEX FROM users-v1 TO users-v2" }
```

With a query body restricting which docs are reindexed:

```json
{
  "statement": "REINDEX FROM users-v1 TO users-v2 WITH BODY $onlyActive",
  "bodies": {
    "onlyActive": {
      "source": {
        "query": { "term": { "active": true } }
      }
    }
  }
}
```

Opt out of `op_type: create` (rare; PR audit trail required):

```json
{
  "statement": "REINDEX UNSAFE(\"intentional overwrite -- dst is empty per script-001\") FROM users-v1 TO users-v2"
}
```

### MIGRATE INDEX (composite, featured)

```
MIGRATE INDEX <old> TO <new>
  [WITH TEMPLATE <id> | WITH BODY $body]
  [VIA ALIAS <alias>]
  [TIMEOUT <duration>]
```

The canonical answer to "how do I propagate a template/mapping change to existing data?" Decomposes at parse time into:

1. `CREATE INDEX <new>` -- body resolved either from `WITH TEMPLATE <id>` (runtime `GET /_index_template/<id>`) or `WITH BODY $body` (sibling reference). Mutually exclusive.
2. `REINDEX FROM <old> TO <new>` with `op_type: create` auto-injected.
3. `ALIAS SWAP <alias> FROM <old> TO <new>` (only when `VIA ALIAS` is present).

Without `VIA ALIAS`, no swap is performed -- the author retains responsibility for cutover. Without `WITH TEMPLATE` or `WITH BODY`, `CREATE INDEX` runs with no body (the cluster's own template-matching may apply).

`MIGRATE INDEX a TO a` (same source and destination) is rejected at parse time. Failure of any sub-statement halts the composite and feeds the partial-rollback ledger semantics.

Template-driven, with cutover:

```json
{
  "statement": "MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template VIA ALIAS users-current TIMEOUT 5m"
}
```

Body-driven, no cutover (author does the alias swap separately):

```json
{
  "statement": "MIGRATE INDEX users-v1 TO users-v2 WITH BODY $newShape",
  "bodies": {
    "newShape": { "settings": { "number_of_shards": 3 } }
  }
}
```

### CREATE TEMPLATE / DROP TEMPLATE

```
CREATE TEMPLATE <name> [WITH BODY $body]
DROP TEMPLATE <name> [IF EXISTS]
```

Composable index templates (`PUT /_index_template/<name>`).

```json
{
  "statement": "CREATE TEMPLATE users-template WITH BODY $template",
  "bodies": {
    "template": {
      "index_patterns": ["users-*"],
      "template": {
        "settings": { "number_of_shards": 3, "number_of_replicas": 1 },
        "mappings": {
          "properties": {
            "id":    { "type": "keyword" },
            "email": { "type": "keyword" }
          }
        }
      },
      "composed_of": ["common-mappings"]
    }
  }
}
```

### CREATE COMPONENT / DROP COMPONENT

```
CREATE COMPONENT <name> [WITH BODY $body]
DROP COMPONENT <name> [IF EXISTS]
```

Component templates (`PUT /_component_template/<name>`). The `IF EXISTS` guard on drops uses a HEAD probe; missing names skip cleanly. Component drops fail loudly when the component is referenced by an index template (drop the referencing template first).

```json
{
  "statement": "CREATE COMPONENT common-mappings WITH BODY @bodies/common-mappings-component.json"
}
```

### CREATE POLICY (ISM)

```
CREATE POLICY <id> [WITH BODY $body]
```

Uploads the policy to `_plugins/_ism/policies` (or `_opendistro/_ism/policies` on older AWS Managed domains -- the provider detects this at bootstrap).

```json
{
  "statement": "CREATE POLICY hot-warm-cold WITH BODY @hot-warm-cold-policy.json"
}
```

`CREATE POLICY` is **idempotent**. ISM versions policies internally, so a plain `PUT` to an already-existing policy returns HTTP 409 `version_conflict_engine_exception`. The dispatcher transparently handles this: on 409 it reads the current `_seq_no` and `_primary_term` from the existing policy and retries the `PUT` with `if_seq_no` / `if_primary_term` query parameters. The result is upsert semantics -- no behavior change when the policy doesn't exist; safe re-execution when it does. This makes `CREATE POLICY` usable inside `[Migration(N, journal: false)]` reconciliation migrations that re-run on every startup. A second 409 on the retry indicates a concurrent writer between the GET and the retry PUT and is surfaced as a hard failure (the migration lock should make this rare).

### APPLY POLICY (ISM)

```
APPLY POLICY <id> TO <pattern> [NO WAIT("<reason>")]
```

Attaches the policy to existing indices matching the pattern via `_plugins/_ism/add`. The dispatcher inspects the response body and surfaces logical failures explicitly: HTTP 200 with `updated_indices: 0` is mapped to `Failed`, not silent OK.

```json
{ "statement": "APPLY POLICY hot-warm-cold TO logs-*" }
```

#### Three temporal scopes for ISM attachment

ISM attachment to an index series isn't one problem with three solutions -- it's three different problems, each with its own right tool. Pick by *when* the indices that need the policy come into existence relative to the migration that owns the policy.

| Scope | Right tool | Sample | Notes |
|---|---|---|---|
| **Greenfield** -- attach to indices that will be created in the future | `ism_template.index_patterns` in the policy body, `template.aliases` in the index template | 9000 -- `ForwardAttachmentLifecycle` | Cluster handles it lazily at index-creation time. No migration runtime cost. Won't help with indices that already exist when the migration runs. |
| **One-time backfill** -- attach a policy to a set of indices that already exist at migration run time | Runtime `APPLY POLICY <id> TO <pattern>` in a normal `[Migration(N)]` | 4000 -- `IsmPolicyAndApply` | Single-shot, journaled. Wildcards adapt to current cluster state at run time. Zero-updated -> `Failed` escalation makes it loud when the pattern matches nothing. |
| **Ongoing reconciliation** -- keep all matching existing indices on the current policy as the policy evolves | Runtime `APPLY POLICY <id> TO <pattern>` in a `[Migration(N, journal: false)]` | 9001 -- `OngoingPolicyReconciliation` | Re-runs on every startup. Idempotent on the wire (ISM's `change_policy` is a no-op for already-on-policy indices). The wildcard form is correct because the set of indices to reconcile changes as new ones roll over and old ones are deleted. |

The three are stackable. A typical mature pipeline uses **greenfield** at install time, **one-time backfill** when an existing series first adopts the policy, and **ongoing reconciliation** as the policy definition evolves over the project's lifetime. Many pipelines never need more than one -- but you should choose deliberately rather than reach for runtime `APPLY POLICY` by default.

Caveat: `ism_template` inside a policy body is the modern endpoint shape. Older AWS-managed clusters served by the legacy `_opendistro/_ism` endpoint may not honor it; if `IsmEndpointDetectStep` resolves to the legacy endpoint, the greenfield row falls back to runtime `APPLY POLICY` (sample 4000's pattern, run once at install time, plus sample 9001's reconciliation pattern for ongoing changes). Modern OpenSearch (2.x and the modern AWS endpoint) supports `ism_template` natively.

### DETACH POLICY (ISM)

```
DETACH POLICY FROM INDEX <pattern> [NO WAIT("<reason>")]
```

Removes an ISM policy attachment from any index matching the pattern via `_plugins/_ism/remove`. Counterpart to `APPLY POLICY`. Wildcards adapt to current cluster state at run time so a single statement can detach an entire index family.

```json
{ "statement": "DETACH POLICY FROM INDEX logs-*" }
```

Unlike `APPLY POLICY`, zero-match (`updated_indices: 0`) is treated as an idempotent no-op (informational log, `Executed` outcome) rather than a failure. Operator teardown scripts routinely detach from patterns that may have already been cleaned up; failing the migration on every clean-state re-run would defeat the purpose. Logical cluster-side failures (`failures: true` in the response body) are still escalated to `Failed`.

### DROP POLICY (ISM)

```
DROP POLICY <id> [IF EXISTS]
```

Deletes the ISM policy definition via `DELETE _plugins/_ism/policies/<id>`. The cluster rejects with HTTP 409 if any index still references the policy -- the canonical lifecycle for retiring a policy is `DETACH POLICY FROM INDEX <pattern>` followed by `DROP POLICY <id>`.

```json
{ "statement": "DROP POLICY hot-warm-cold-deprecated IF EXISTS" }
```

`IF EXISTS` short-circuits with `Skipped` when the policy isn't present (ISM has no `HEAD` verb, so the dispatcher probes with `GET` and treats any non-200 as absent). Combined with `DETACH POLICY`, this gives a teardown sequence that's safe to re-run on partially-cleaned clusters.

### WAIT FOR (cluster health)

```
WAIT FOR <green|yellow> [ON <idx>] [TIMEOUT <duration>]
```

`WAIT FOR YELLOW` is the documented "not red" idiom -- there is no separate "WAIT FOR not red" verb. The default health threshold is `Yellow`; `WithProductionDefaults()` flips it to `Green`.

```json
{ "statement": "WAIT FOR YELLOW ON users TIMEOUT 30s" }
```

### WAIT UNTIL TASK

```
WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]
```

Polls `_tasks/<id>` with exponential backoff (500ms -> 30s ceiling). Used by long-running operations that surface a task id (e.g., reindex async dispatch).

```json
{ "statement": "WAIT UNTIL TASK r1A2B3C4D:42 COMPLETE TIMEOUT 10m" }
```

### WHEN VERSION (conditional)

```
WHEN VERSION <op> '<version>' <statement>
```

Statement-level prefix that gates the wrapped child on the live cluster's reported version. Comparators: `=`, `!=`, `<`, `<=`, `>`, `>=`. The cluster version is fetched once per dispatcher (cached) and compared semantically -- `'2.9' < '2.10'` is true (lexical comparison would invert it). Skipped statements log the actual cluster version so ops can distinguish "cluster older than expected" from "predicate is wrong."

v1 supports `MAJOR.MINOR[.PATCH]` only. `-SNAPSHOT`, `-rc<N>`, and AWS `OpenSearch_<x>` prefix/suffix forms are rejected at parse time with a remediation message.

```json
{
  "statements": [
    { "statement": "WHEN VERSION >= '2.10' CREATE TEMPLATE users-v2 WITH BODY $modernTemplate" }
  ]
}
```

## Implicit waits and the NO WAIT modifier

`OpenSearchMigrationOptions.WaitMode` controls when the implicit cluster-health wait fires after each mutating verb:

| Mode | When it waits | Use when |
|------|---------------|----------|
| `PerStatement` (library default) | After every mutating statement, scoped to the mutated index | Dev iteration, small migrations |
| `PerMigration` (production) | One consolidated wait at end of resource pass, scoped to all dirty indices | Production -- avoids the N+1 master-task-queue storm on long migrations |
| `Off` | Never (only explicit `WAIT FOR` runs) | Author owns all wait timing |

The five mutating verbs that participate are `CREATE INDEX`, `REINDEX`, `ALIAS SWAP`, `UPDATE SETTINGS`, and `APPLY POLICY`. Each accepts an optional `NO WAIT("<reason>")` modifier as the very last clause:

```
CREATE INDEX users WITH BODY @bodies/users.json NO WAIT("massive mapping; manual wait via dashboards")
REINDEX FROM users-v1 TO users-v2 NO WAIT("Tasks API polling out of band")
```

`NO WAIT` skips the implicit wait for that one statement under `PerStatement`. Under `PerMigration`, per-statement `NO WAIT` is a DEBUG-level no-op (only the end-of-migration flush runs). Bare `NO WAIT` (no parentheses, no justification) is rejected at parse time -- the justification token is the high-signal grep target for PR review and incident postmortems, mirroring the `UNSAFE("...")` precedent.

## Context filter

A `statements.json` file may declare an optional top-level `context` array. The runner uses this to gate the entire file against `OpenSearchMigrationOptions.ActiveContext` (a comma-separated string, bindable via `Migrations:ActiveContext`).

```json
{
  "context": ["prod", "staging"],
  "statements": [
    { "statement": "CREATE INDEX users WITH BODY @bodies/users-mapping.json" }
  ]
}
```

Resolution rules:

| File context | `ActiveContext` | `ContextResolutionPolicy` | Outcome |
|--------------|-----------------|---------------------------|---------|
| (none) | (any) | (any) | run |
| `["prod"]` | `"prod"` | (any) | run |
| `["prod","staging"]` | `"canary,prod"` | (any) | run (any tag matches) |
| `["prod"]` | `"dev"` | (any) | skip (INFO log) |
| `["prod"]` | `null` | `SkipIfUnset` (library default) | skip (INFO log) |
| `["prod"]` | `null` | `RequireExplicit` (production) | throw `MissingActiveContextException` |

`WithProductionDefaults()` flips `ContextResolutionPolicy` to `RequireExplicit` so production deployments fail loudly when `ActiveContext` is missing. Matching is case-sensitive -- context tags are identifiers. The check is per-file: skipped files do not dispatch any statements (Up) or run any rollbacks (Down). Combine with `WHEN VERSION` for finer-grained statement-level gating within a file that has already been admitted by context.

## Rollback

Each statement entry may carry an optional `rollback` field. UpAsync runs `statement` fields in declaration order; DownAsync (via `RollbackStatementsFromAsync`) runs `rollback` fields in reverse declaration order -- last operation applied is the first to undo.

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

If the rollback halts partway (statement N fails after N+1..M succeeded), the ledger entry is overwritten to `partially_rolled_back` with `failedStatementIndex: N`, and subsequent runs require `ForceResume = true` (`--force-resume` on the runner CLI). See the [AWS validation runbook](../runbooks/opensearch-aws-validation.md) for the recovery protocol.

## Bulk loading

Bulk-load helper for code migrations that need to seed documents efficiently. Wraps `BulkAllObservable` with production-safe defaults (8x parallelism, 1s exponential backoff, 5 retries on 429, refresh-once-at-end). Each retried 429 surfaces as a structured WARN log so operator dashboards can spot self-induced-throttling patterns.

```csharp
[Migration( 5000 )]
public class SeedDocuments( OpenSearchResourceRunner<SeedDocuments> runner ) : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        var docs = LoadFromCsv();   // or any IEnumerable<T>

        await runner.BulkLoadAsync( "users", docs, options =>
        {
            options.BatchSize             = 1000;
            options.MaxDegreeOfParallelism = 8;
            options.BackOffRetries        = 5;
            options.InitialBackOff        = TimeSpan.FromSeconds( 1 );
            options.RefreshOnCompleted    = true;
        }, ct );
    }
}
```

## Locking

The provider uses a single OpenSearch document on `LockIndex` for distributed locking. Acquisition is `op_type=create` (atomic claim); on conflict, a realtime GET checks staleness before any takeover. The renewal loop refreshes the heartbeat at `LockRenewInterval`; CAS conflicts on renewal signal that another runner has taken over and the in-flight migration is canceled cleanly. `LockMaxLifetime` caps total wall-clock hold so a hung migration cannot lock forever.

The lock index is created with `number_of_replicas: 0` so concurrent acquire under N runners does not stall on replica-write coupling.

## Ledger forensics

The migration ledger captures forensic fields per R-06 so post-mortems have what they need without log spelunking:

| Field | Purpose |
|-------|---------|
| id | Record id (`record.<version>.<kebab-name>`) |
| runOn | Apply timestamp |
| direction | Up or Down |
| status | succeeded, failed, or partially_rolled_back |
| appliedBy | `<machineName>/<processId>` |
| error | Failure detail, when applicable |
| failedStatementIndex | Which rollback statement halted the Down sequence |

## Squash support

The OpenSearch provider ships full squash codegen via `RestStateDiffStrategy`. The canonical output is JSON-section form (`[index_template]`, `[component_template]`, `[index_metadata]`, `[alias]`, `[ism_policy]`, `[ingest_pipeline]`, etc.) because OpenSearch structural state is irreducibly JSON-bodied. The capture path probes the live cluster via REST endpoints; the canonicalizer strips ephemeral catalog fields (`creation_date`, `uuid`, `version`, `provided_name`, `policy_version`, `last_updated_time`, `seq_no`, `primary_term`) at every nesting level.

**Painless preservation:** painless script source rides through as opaque JSON string content -- the canonicalizer never parses or modifies painless. Opaque-string preservation plus structural JSON canonicalization means no painless parser, no operator annotation, and no fallback are needed.

The Roslyn-based `OpenSearchMigrationSourceScanner` enforces the `[DataMigration]` / `[StructuralOnly]` annotation requirement for migrations using receiver-anchored `_client.*` write call-sites (`Index*`, `Update*`, `UpdateByQuery*`, `Delete*`, `DeleteByQuery*`, `Bulk*`, `Reindex*`).

See [Squashing migrations](squashing-migrations.md) for the cross-provider squash CLI + workflow.

## Production deployment

The companion runner project (`runners/Hyperbee.MigrationRunner.OpenSearch`) is the recommended deployment shape. Same Helm chart / Dockerfile / Octopus deploy template as the other Hyperbee runners. CLI flags: `--connection`, `--auth-mode`, `--user`, `--password`, `--api-key-id`, `--api-key`, `--client-cert`, `--client-cert-password`, `--ledger`, `--lock`, `--lock-name`, `--profile`, `--file`, `--assembly`, `--force-resume`. See [Runners](runners.md).

## Multi-topology testing

- Single-node Testcontainers (every PR) covers the grammar surface.
- 3-node multi-node Testcontainers Compose (every PR via `multi_node_tests.yml` in CI) covers the production behaviors single-node cannot exercise: GREEN threshold, replica allocation, shard relocation under load, lock-index `replicas:0` invariant.
- AWS Managed OpenSearch is validated via the [AWS validation runbook](../runbooks/opensearch-aws-validation.md), pre-release and nightly when AWS credentials are available in CI.

See `tests/Hyperbee.Migrations.Integration.Tests/Container/OpenSearch/MULTINODE.md` for how to use the multi-node harness in your own tests.

## Samples

`runners/samples/Hyperbee.Migrations.OpenSearch.Samples` ships eight sample migrations covering every v1 verb. Sample 6 (`MigrateIndexComposite`) is featured -- it is the canonical answer to "how do I propagate mapping changes to existing data?" See [Resource Migrations](resource-migrations.md).
