# Research: Migration Squashing for Hyperbee.Migrations

**Date:** 2026-05-04
**Status:** Historical — supersedes by destructive-model reframe (2026-05-05)
**Author:** Brenton Farmer (with research agents)
**Related:** [ADR-0019](../decisions/0019-migration-squash-replaces-graph.md), [canonical design](../design/migration-squashing.md)
**Question:** How should Hyperbee.Migrations support migration squashing (compacting N migrations into one or a few equivalents) in a way that works across its provider mix (OpenSearch, Postgres, MongoDB, Couchbase, Aerospike), preserves authorial intent, handles mid-range environments without manual hand-syncing, and remains robust against the universal blind spots of data migrations and vendor-specific features?

> **NOTE 2026-05-05.** This document uses "rollup" terminology throughout because
> it predates the vocabulary alignment to "squash" (operator-initiated codegen +
> replace). The cross-ecosystem survey, findings, and recommendation remain
> useful as background context — but several specific recommendations in this
> document (the additive "originals stay" model, the four-phase phased approach,
> the deprecation-window discipline) were superseded on 2026-05-05 when the
> design adopted a destructive Flyway/Atlas-style codegen workflow. See
> [ADR-0019](../decisions/0019-migration-squash-replaces-graph.md) for the
> authoritative current design. This research artifact is retained as historical
> record of the design exploration — its survey of EF Core, Django, Flyway,
> Liquibase, Prisma, Sqitch, Knex, Atlas, and PRISM remains valid prior-art.

## Purpose

When the migration list grows large (the canonical EF/Django pain point is "fresh DB takes minutes to provision because the runner walks 500 migrations"), teams want to collapse a contiguous range of migrations into one or a few equivalent migrations. The goals are: faster fresh-environment provisioning, smaller working set in the migrations folder, and preserved correctness for environments at any cut-point in the un-squashed range.

Hyperbee.Migrations does not have this feature today. Sample migrations across providers top out at 10 ([OpenSearch samples](../../runners/samples/Hyperbee.Migrations.OpenSearch.Samples/Migrations/)), so the feature is forward-looking — but the design decisions that enable it (checksums, replaces-graph, integrity hashing) are easier to land *now*, before history accumulates, than retrofit later.

This document captures the cross-ecosystem survey, identifies the key design dimensions, compares strategies, and recommends a phased approach. It does not commit to an implementation — that is the role of the follow-on `/nop:propose`.

---

## Sources Examined

| Source | Type | Key Finding |
|--------|------|-------------|
| [Django `squashmigrations`](https://docs.djangoproject.com/en/5.1/topics/migrations/#squashing-migrations) | External docs | Only tool with first-class operation-list fusion + `replaces=[…]` directive that automatically catches up environments at any cut-point |
| [EF Core managing migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing) | External docs | No first-class squash; manual workflow via `Remove-Migration` + snapshot regeneration |
| [dotnet/efcore #2174](https://github.com/dotnet/efcore/issues/2174) | GitHub issue | 11-year-old open issue for first-class squash; team has stated robust impl needs `replaces`-style memory |
| [dotnet/efcore #33118](https://github.com/dotnet/efcore/issues/33118) | GitHub issue | Recent "consolidate migrations" tracking issue; same blockers |
| [Rails ActiveRecord migrations](https://guides.rubyonrails.org/active_record_migrations.html) | External docs | `schema.rb` / `structure.sql` is the de facto baseline; `db:schema:load` skips migrations entirely on fresh installs |
| [`squasher` gem](https://github.com/jalkoby/squasher) | External repo | Community Rails squash automation — merge migrations created before year X into one |
| [Flyway baselines and consolidations](https://www.red-gate.com/hub/product-learning/flyway/flyway-baselines-and-consolidations) | External docs | `B__` baseline migrations skipped above-baseline by version comparison; "CDRB" workflow (Create-Delete-Rename-Baseline) |
| [Flyway repeatable migrations](https://documentation.red-gate.com/fd/repeatable-migrations-273973335.html) | External docs | `R__` orthogonal mechanism for views/procedures — re-applied on checksum change |
| [Liquibase generateChangelog](https://docs.liquibase.com/commands/inspection/generate-changelog.html) | External docs | Live-DB introspection emits a changelog; vendor-specific DDL is the documented weak spot |
| [Liquibase diff-changelog](https://docs.liquibase.com/commands/inspection/diff-changelog.html) | External docs | Diff-based consolidation; recommended only as sanity check, not primary source |
| [Prisma squashing migrations](https://www.prisma.io/docs/orm/prisma-migrate/workflows/squashing-migrations) | External docs | Two flavors (dev reset vs. production diff); manual `migrate resolve --applied` for partial-rollup |
| [Sqitch rework](https://sqitch.org/docs/manual/sqitch-rework/) | External docs | Design rejects history rewriting; no rollup; provenance-first stance |
| [Knex migrations](https://knexjs.org/guide/migrations.html) | External docs | No first-class squash; community pattern is `pg_dump` baseline + hand-stamp ledger |
| [knex/knex #2728](https://github.com/knex/knex/issues/2728) | GitHub issue | "Happy Path for Squashing Migrations Together" — open since 2018, unresolved |
| [Atlas declarative-vs-versioned](https://atlasgo.io/concepts/declarative-vs-versioned) | External docs | Desired-state HCL is diff base; `atlas.sum` Merkle integrity file; `--baseline` flag for env catch-up |
| [Atlas migrate diff](https://atlasgo.io/versioned/diff) | External docs | Internal "dev database" replays migration directory to compute deltas |
| [Curino, Moon, Zaniolo — PRISM (VLDB 2008)](https://www.vldb.org/pvldb/vol1/1453939.pdf) | Academic paper | Schema Modification Operators (SMO) algebra: composition, inversion, query rewriting across schema versions |
| [Herrmann et al. — Living in Parallel Realities (SIGMOD 2017)](https://dl.acm.org/doi/10.1145/3035918.3064046) | Academic paper | Co-existing schema versions; extends PRISM's query-rewriting to mixed-environment catch-up |
| [`MigrationRunner.cs:160-189`](../../src/Hyperbee.Migrations/MigrationRunner.cs) | Internal codebase | Provider-agnostic version ordering and reflection discovery — directly reusable for rollup range selection |
| [`IMigrationRecordStore.cs`](../../src/Hyperbee.Migrations/IMigrationRecordStore.cs) | Internal codebase | Five-method ledger contract; record shape is `(Id, RunOn)` only — **no checksum** |
| [`MigrationRecord.cs`](../../src/Hyperbee.Migrations/MigrationRecord.cs) | Internal codebase | Confirms minimal record shape; rollup verification needs more |
| [`OpenSearchStatementParser.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Grammar/OpenSearchStatementParser.cs) + [`Internal/Ast/`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Ast/) | Internal codebase | OpenSearch is the *only* provider with a parser-AST-dispatcher pipeline; this enables Django-style operation fusion *for OpenSearch only* |
| [`OpenSearchExceptions.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchExceptions.cs) | Internal codebase | OpenSearch is the only provider with formal rollback infrastructure (R-19 partial-rollback state) — relevant to rollup-of-Down |
| [`tests/.../Container/`](../../tests/Hyperbee.Migrations.Integration.Tests/Container/) | Internal codebase | Per-provider Testcontainers wrappers — directly reusable for ephemeral snapshot derivation |

---

## Findings

### Finding 1: Two Dominant Strategies + One Hybrid

Across .NET, Python, Ruby, Java, JS, and Go ecosystems, rollup tooling falls into three buckets:

1. **Operation-list fusion.** The tool reads the AST/operation-list of every migration in the range, runs a deterministic optimizer, and emits a new migration that is the algebraic composition of the originals. Django's `squashmigrations` is the only rigorous implementation. The optimizer fuses pairs (`CreateModel`+`AddField` → `CreateModel(fields=[...])`, `AddField`+`RemoveField` → ∅, `AlterField`+`AlterField` → last write wins).

2. **Live-DB / model-snapshot diff.** The tool runs the migrations against an empty environment (or reads a model description) and emits a single migration that recreates the resulting state. EF Core (model snapshot regeneration), Prisma (`migrate diff --from-empty`), Liquibase (`generateChangelog`), Rails (`schema.rb` / `pg_dump`), Flyway (`B__` files; Enterprise auto-generates), and Atlas (`migrate diff` against empty) all use this approach. Output is whatever the introspector or model serializer can see; vendor-specific features and data migrations are silently discarded.

3. **Hybrid (Atlas).** Desired-state HCL is the diff base, but versioned files exist alongside. `atlas.sum` provides Merkle-style directory integrity; `atlas migrate apply --baseline <version>` provides cut-point catch-up. Closest to "best of both" but still loses imperative side-effects.

**Sqitch deliberately rejects rollup.** Its design treats history as immutable provenance. The underlying anxiety (rollup destroys history) is valid, but the answer in tools that *do* rollup is "archive, don't delete" — keep replaced files in source control history with a manifest mapping rolled-up file → contributing originals.

**Source:** Django docs, EF Core docs + GitHub issues, Rails guide, Flyway/Liquibase/Prisma/Atlas docs, Sqitch docs.

### Finding 2: Django's `replaces=[…]` Is the Canonical Partial-Rollup Mechanism

The single most-copied design idea in this space — and absent from every other tool. When Django emits a squashed migration, it includes a `replaces = [(app, '0001_initial'), …]` attribute listing every original migration the squash subsumes. At runtime:

- A **fresh database** sees only the squashed migration in the graph (the leaves are the squashed names) and applies it once.
- An **environment whose `django_migrations` table already contains some/all of the replaced names** continues forward through any unreplaced migrations, then on the next migrate, Django sees that all entries in `replaces` are present and *transparently records the squashed migration as applied without re-running it*.

This is why Django can ship a squash without coordinating fleets — every other tool requires either hand-stamping the ledger on each environment (EF, Rails, Knex, Prisma's `migrate resolve --applied`, Liquibase's `changelog-sync`) or relying on a version comparison that only works for monotonic linear histories (Flyway, Atlas's `--baseline`).

The cost is one extra column in the ledger (`replaces` ↔ `replaced_by` linkage) and one extra check at migrate time. It is the highest-leverage design decision a rollup feature can make.

**Source:** Django `squashmigrations` documentation; cross-reference: every other tool surveyed lacks an equivalent.

### Finding 3: Data Migrations Are the Universal Blind Spot

Only Django names the problem. Its `RunPython` and `RunSQL` operations are *opaque* to the squash optimizer and pass through verbatim — meaning N data migrations remain N operations after squash unless the author explicitly marks them `elidable=True`, in which case the optimizer drops them on the assumption that a fresh install reaching the squash will not need the data fix-up (because the rows it patched do not yet exist).

Every other tool either:
- tells operators to re-add hand-written SQL after squash (Prisma docs are explicit: "any manually changed or added SQL in your `migration.sql` files will not be retained"; Flyway/Liquibase same advice);
- treats migrations as pure DDL by axiom and silently drops side-effects (EF Core, Atlas, Knex);
- has no statement on it (Rails — the assumption is that `schema.rb` only ever held DDL anyway, and data migrations are rake tasks).

For Hyperbee.Migrations, data migrations are not hypothetical — they're common across NoSQL providers (Couchbase seed data, MongoDB validator backfills, OpenSearch reindex-with-script). The author-opt-in flag (Django's `elidable`) is the only pattern that handles this honestly.

**Source:** Django docs; Prisma squashing docs; Flyway/Liquibase docs; cross-reference: hyperbee sample migrations include data-bearing operations across providers.

### Finding 4: Snapshot Diff Loses Vendor-Specific Features

Each tool's snapshot mechanism captures a different subset of schema state. The differences matter because rollup's correctness condition is "applying the rollup yields a state byte-equivalent to applying the originals":

| Tool | What snapshot captures | Documented losses |
|------|------------------------|-------------------|
| **EF Core** ModelSnapshot | tables, columns, FKs, indexes, model-level constraints | triggers, views, vendor-specific types, raw `Sql()` blocks, permissions, sequences not on PKs |
| **Liquibase** generateChangelog | tables, columns, indexes, FKs, views, sequences, procs, triggers (with caveats per dialect), packages | row-level security policies, partition definitions in some dialects |
| **Atlas** HCL/SQL | tables, columns, indexes, FKs, views, triggers, sequences, check constraints; per-dialect: materialized views, partitions, RLS | data, grants/permissions in some dialects, arbitrary `DO $$ ... $$` blocks |
| **Rails** `schema.rb` | tables, columns, indexes, FKs only | triggers, procs, views explicitly — hence the `structure.sql` escape hatch |
| **Prisma** schema.prisma | whatever the Prisma model knows | triggers, views, permissions, partial indexes beyond what the schema syntax models |

For Hyperbee.Migrations, the analogous question is provider-by-provider. **OpenSearch** has the richest queryable state: index mappings + templates + component templates + ISM policies + aliases + ingest pipelines. **Couchbase / MongoDB** have partial schema (collections, indexes, validators). **Aerospike** has minimal schema (namespaces, sets, secondary indexes). **Postgres** has the territory all the prior art assumes.

A snapshot-based rollup must explicitly enumerate per provider what counts as "schema" and what is silently dropped. ISM policies in OpenSearch — yes, those count. Saved searches — definitely not. The decision must be explicit, not auto-detected.

**Source:** EF Core, Liquibase, Atlas, Rails, Prisma docs; cross-reference: hyperbee provider inventory.

### Finding 5: Atlas's `atlas.sum` Is the Missing Integrity Primitive

Atlas computes a Merkle-style hash over the migration directory and stores it in `atlas.sum`. Any out-of-band edit (a hand-modified migration file, a deleted file, a re-ordering) invalidates the sum. The runner refuses to apply a directory whose sum doesn't match.

This is exactly the integrity guarantee a rollup operation needs: rollup *deletes files and replaces them*. Without an integrity primitive, a partial-rollup-in-progress repo (some files deleted, new file written, but not committed) is silently broken. With one, the rollup tool can verify "the directory you're about to apply is the directory we tested" before doing destructive work.

No other tool in the survey has this. EF Core, Flyway, Liquibase, Prisma all rely on the per-file checksum in their ledger — which catches file modification but doesn't catch directory re-organization.

**Source:** Atlas docs; cross-reference: missing from every other tool.

### Finding 6: PRISM Is the Formal Version of Operation Fusion

Curino, Moon, and Zaniolo's PRISM workbench (VLDB 2008) formalizes schema evolution as a sequence of *Schema Modification Operators (SMOs)* — `RENAME COLUMN`, `MERGE TABLE`, `PARTITION TABLE`, etc. — over which they define composition, inversion, and *query rewriting across schema versions*. PRISM++ extends this to integrity constraints. Herrmann et al.'s "Living in Parallel Realities" (SIGMOD 2017) generalizes the query-rewriting half to mixed-environment catch-up.

The relevance to rollup: their composition algebra is the rigorous version of Django's optimizer (`AddField` ∘ `RemoveField` = identity, etc.). Their query-rewriting is the rigorous version of Django's `replaces`-graph catch-up. No tool surveyed has implemented the SMO algebra; Django's optimizer is the closest practical instance, and it doesn't go nearly as far.

Worth knowing this exists for two reasons: (a) it confirms the practical heuristics aren't ad-hoc — there's a theory underneath, (b) it gives a vocabulary for future-proofing decisions ("our fusion is a partial implementation of SMO composition; we explicitly don't handle inversion or cross-version query rewriting").

**Source:** PRISM (VLDB 2008); PRISM++ (VLDB Journal 2013); Herrmann et al. (SIGMOD 2017).

### Finding 7: Hyperbee's Internal State — What's There, What's Missing

What hyperbee already has that rollup can reuse:
- **Provider-agnostic version ordering** at [`MigrationRunner.cs:160-189`](../../src/Hyperbee.Migrations/MigrationRunner.cs) — directly enumerable for rollup range selection.
- **`IMigrationRecordStore` contract** ([`IMigrationRecordStore.cs`](../../src/Hyperbee.Migrations/IMigrationRecordStore.cs)) — provider-agnostic ledger API; rollup ledger entries use the same read/write/delete methods.
- **OpenSearch parser/AST/dispatcher** ([`Internal/Grammar/`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Grammar/), [`Internal/Ast/`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Ast/), [`Internal/Dispatch/`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Dispatch/)) — 21 statement AST types; **the only provider where Django-style operation fusion is feasible**.
- **Test container infrastructure** ([`tests/.../Container/`](../../tests/Hyperbee.Migrations.Integration.Tests/Container/)) — per-provider Testcontainers wrappers; directly reusable for ephemeral snapshot derivation.
- **OpenSearch bootstrap pipeline** (`IBootstrapStep` chain) — reusable model for multi-step rollup workflows.

What hyperbee is **missing** that rollup needs:
- **Checksum on `MigrationRecord`** — currently just `(Id, RunOn)` per [`MigrationRecord.cs`](../../src/Hyperbee.Migrations/MigrationRecord.cs). Rollup with `replaces=[…]` requires cryptographic confidence that "this ledger row records what we think it records." Adding `Checksum` (hash of the migration's effective body) costs little and pays off later.
- **A `Replaces=[…]` capability on `[Migration]`** — either a new attribute or an optional parameter on the existing one.
- **A rollup-aware ledger marker** — record kind enum (`Migration`, `Rollup`, `Baseline`) so audits can distinguish.
- **Per-provider snapshot/introspection contract** — currently zero introspection code outside of OpenSearch's `LedgerIndexInitStep` mapping verification. Largest implementation lift.
- **Integrity primitive** — directory hash analogous to Atlas's `atlas.sum`.

**Source:** Internal codebase survey via Explore agent.

### Finding 8: Down-of-Rollup Is Hardest; Up-Only Is the Industry Standard

Hyperbee is unusual in supporting `DownAsync()` as a first-class concept. OpenSearch has the richest infrastructure ([`OpenSearchPartialRollbackException`](../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchExceptions.cs), R-19 partial-rollback ledger state, statement-level inverse operations); other providers treat `DownAsync()` as optional and rarely implement it.

The question of "what is the inverse of a rollup?" has no clean answer. The composition of N inverses is not generally expressible as a single inverse — and even where it is, the data state after the original Up is not reconstructible from after-images. **Every prior-art tool that supports rollup is implicitly Up-only for the rollup itself**: Django's squash has no Down; Flyway baseline scripts cannot be rolled back; Prisma's squash documentation says nothing about Down.

The honest answer for hyperbee: **rollups are Up-only**. Authors who need to roll back across a rollup boundary must restore from backup. This matches industry practice and avoids designing a feature that cannot be made correct.

**Source:** Cross-reference of every tool's rollup documentation; internal: [`OpenSearchExceptions.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchExceptions.cs).

---

## Comparison

### Strategies for Generating the Rolled-Up Migration

| Dimension | Operation Fusion (Django) | Snapshot Diff (EF/Prisma/Liquibase/Rails/Flyway/Atlas) | Hybrid (Atlas full) |
|-----------|---------------------------|--------------------------------------------------------|---------------------|
| Source of truth for the new migration | The original migration ASTs | A live database / model description | Both: HCL desired-state + versioned files |
| Preserves authorial intent | Yes — fusion is on author-written ops | No — snapshot loses the path taken | Partial — desired-state captures structure; loses path |
| Preserves data migrations | Yes (verbatim, or `elidable` to drop) | No (silently dropped) | No |
| Preserves vendor-specific features | Yes (verbatim if not fused) | Variable per snapshot capability — usually loses triggers, RLS, etc. | Better than pure snapshot; still loses imperative blocks |
| Requires runtime infrastructure | Parser/AST per provider | Introspection/snapshot per provider | Both |
| Hyperbee feasibility | OpenSearch only (it's the only AST provider) | Possible for all (requires per-provider snapshot code) | OpenSearch (AST + snapshot) |
| Verification | Apply originals + apply fused; compare snapshots | Self-consistent by construction | Round-trip versioned ↔ declarative |

### Mechanisms for Partial-Rollup Catch-Up (Mixed-Environment Fleets)

| Tool | Mechanism | Automatic? | Operator burden |
|------|-----------|------------|------------------|
| **Django** | `replaces=[…]` graph in the squashed migration | Yes — runner sees replaced names in ledger and auto-marks squash as applied | None per-environment |
| **Flyway** | `B__` baseline skipped if version comparison says so | Yes — version comparison | None per-environment, but linear-history-only |
| **Atlas** | `--baseline <version>` flag at apply time | Operator-issued | Once per environment |
| **Prisma** | `migrate resolve --applied` per environment | Manual | Per-environment command |
| **Liquibase** | `changelog-sync` per environment | Manual | Per-environment command |
| **Rails** | Hand-stamp `schema_migrations` or use `db:schema:load` only on fresh envs | Manual | Per-environment SQL |
| **EF Core** | Hand-stamp `__EFMigrationsHistory` | Manual | Per-environment SQL |
| **Knex** | Hand-stamp `knex_migrations` | Manual | Per-environment SQL |
| **Sqitch** | n/a — design rejects history rewriting | n/a | n/a |

### Integrity & Provenance

| Tool | Per-file checksum | Directory integrity | Provenance preservation |
|------|-------------------|---------------------|--------------------------|
| **Django** | No | No | Original files preserved during transition; `replaces=` is the manifest |
| **EF Core** | Migration `Id` only | No | `__EFMigrationsHistory` row only |
| **Flyway** | Yes (`flyway_schema_history.checksum`) | No | History row + checksum |
| **Liquibase** | Yes (`databasechangelog.md5sum`) | No | Per-changeset history |
| **Prisma** | Yes (`_prisma_migrations.checksum`) | No | History row + checksum |
| **Atlas** | Yes | **Yes (`atlas.sum`)** | History + directory hash |
| **Hyperbee (today)** | **No** | No | `(Id, RunOn)` only — no checksum |

---

## Recommendation

A single universal rollup mechanism would be wrong for hyperbee given the provider mix. The right shape is **a small core scaffolding plus per-provider strategies, delivered in phases**.

### Phase 1 — Core Scaffolding (low-risk, ships independently, no-regrets)

Land the data-shape and contract changes that *any* future rollup strategy will need. Without these, every later phase is harder.

- **Add `Checksum` (string, nullable) to `MigrationRecord`.** Populate on write going forward. Existing ledger rows with null checksum mark the "pre-checksum era"; rollup logic refuses to operate against null-checksum history without an explicit `--accept-unverified` flag.
- **Add a `Replaces` parameter to `[Migration]`** (or a new `[RollupMigration]` attribute — to be decided in `/nop:propose`). Empty by default.
- **Modify `MigrationRunner.DiscoverMigrations`** so that when about to apply a migration with `Replaces=[…]`: if all replaced versions are already in the ledger, transparently mark the rollup as applied and skip its `UpAsync`; if some/none are present, run normally and the rollup serves as a fresh-install fast path. This is Django's runtime behavior, ported.
- **Add a `RecordKind` enum** (`Migration`, `Rollup`, `Baseline`) for audit clarity.
- **Refuse to author rollups for migrations with `DownAsync` overrides without an explicit author opt-in.** Down-of-rollup is unsupported per Finding 8; surfacing this loudly at rollup-creation time is better than silently dropping Down support.

This phase ships independently of any rollup *generator*. It enables hand-authored rollups (Rails-style: "I `pg_dump`'d, here's a baseline migration with `Replaces=[1000, 1010, …]`"). It also gives every later phase the audit infrastructure to verify against. **Adopt now, before history accumulates.**

### Phase 2 — OpenSearch AST Operation Fusion (highest-value, scoped)

OpenSearch is the only provider with a parser, AST, and dispatcher already in place. Django-style operation fusion is uniquely feasible here.

- **Define `IStatementOptimizer` middleware** in [`Internal/Middleware/`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Middleware/) taking `IList<StatementAst>` → fused `IList<StatementAst>`.
- **Implement deterministic fusion rules** (Django's optimizer is the template):
  - `CREATE INDEX X` ∘ `UPDATE MAPPING X` → merged `CREATE INDEX X`
  - `CREATE INDEX X` ∘ `DROP INDEX X` → ∅
  - `CREATE TEMPLATE T` ∘ `CREATE TEMPLATE T'` (same name) → last write wins
  - `ALIAS SWAP a→b` ∘ `ALIAS SWAP b→c` → `ALIAS SWAP a→c`
  - `WAIT FOR HEALTH`, `REFRESH` between schema ops → droppable mid-block
- **Refuse to fuse anything containing `UNSAFE(…)`** without explicit author opt-in. The modifier exists *because* the operation isn't commutative.
- **Treat `REINDEX` as opaque.** Data flow matters; the dest of one is the source of another.
- **Build a `dotnet hyperbee-migrations rollup` CLI verb** (or analogous tooling) that: parses source migrations → fuses → emits a new migration file with merged `statements.json` and correct `Replaces=[…]` → optionally runs verification (apply original chain to ephemeral container, apply fused to second container, compare snapshots).

This is the highest-value path because OpenSearch is the provider whose users will hit migration-list bloat first (long ISM policy + alias + reindex chains) and where the AST already exists.

### Phase 3 — Snapshot Derivation per Provider (larger lift, optional per provider)

For providers without an AST, snapshot-based rollup is the only path. The lift is per-provider introspection code.

- **Define `IProviderSnapshot.CaptureAsync()`** returning a serializable per-provider description.
- **OpenSearch:** `GET /_cat/indices` + per-index `GET /<idx>/_mapping` + `GET /_template/*` + `GET /_component_template/*` + `GET /_plugins/_ism/policies/*` + `GET /_alias/*` + ingest pipelines.
- **Postgres:** shell out to `pg_dump --schema-only --no-owner --no-privileges`.
- **MongoDB:** `db.runCommand({listCollections: 1})` + per-collection `listIndexes`.
- **Couchbase:** query `system:indexes` + bucket/scope/collection metadata.
- **Aerospike:** `info("namespaces")` + `info("sindex")`. Schema is so thin that hand-authored baselines may suffice; this provider may not justify the tooling investment.
- **Add a `--strategy snapshot` mode** to the rollup CLI. Spins up an ephemeral container (reusing [`tests/.../Container/`](../../tests/Hyperbee.Migrations.Integration.Tests/Container/) infrastructure), applies the source range, captures a snapshot, emits a baseline migration whose body re-applies it.

This phase is pay-as-you-go: pick the provider with the largest migration chain in production and ship that strategy first.

### Phase 4 — Verification + Deprecation Tooling (closes the discipline gap)

- **Round-trip verification**: apply original chain to container A, apply rollup to container B, compare snapshot hashes. Refuse to commit a rollup that diverges. This is Atlas's `atlas.sum` insight applied at rollup-creation time rather than at-rest.
- **Audit-aware cleanup**: `--prune` mode that, given a list of known environment ledgers (or a connection string), confirms no environment is on the un-replaced path, then strips `Replaces=[…]` from the rollup attribute and deletes the original files. Automates the two-phase deprecation Django leaves to manual discipline.
- **Archive don't delete**: replaced migrations move to an `archive/` subtree (or live in git history alone) with a manifest mapping rolled-up file → contributing originals. Answers Sqitch's provenance concern without rejecting the feature.

### What to Copy and What to Leave

**Copy:**
- Django's `replaces=[…]` graph and the auto-marking behavior. Single best idea in the field.
- Django's `elidable` flag for data migrations. Best honest answer to the universal blind spot.
- Atlas's `atlas.sum` integrity check — apply at rollup-creation time, not just at-rest.
- Flyway's "version comparison skips the baseline above the existing version" behavior — what the runner does when an environment already has all `Replaces` rows.
- Django's two-phase deprecation discipline.

**Don't copy:**
- EF Core's "delete the migrations folder and regenerate" workflow. No `replaces`, no integrity check, vendor-specific features silently lost. The 11-year-old open issue [dotnet/efcore#2174](https://github.com/dotnet/efcore/issues/2174) is the existence proof that this is hard if you start without checksums and `replaces`.
- Sqitch's "history is immutable" stance — the provenance concern is valid, but the answer is Phase 4's archive strategy, not refusing the feature.
- Liquibase's `generateChangelog` as the *primary* path — its docs explicitly warn it's a sanity check. Use it for verification, not generation.

---

## Open Questions

These are the design decisions that `/nop:propose` should resolve:

1. **Attribute shape: extend `[Migration]` or introduce `[RollupMigration]`?** Extension is simpler to discover; new attribute is cleaner separation. The trade is parser/runner complexity vs. authorial clarity.
2. **Where does fusion live for OpenSearch — middleware or a separate rollup-time tool?** Middleware would let the *runtime* fuse adjacent migrations on-the-fly; a separate tool keeps fusion as a build-time artifact. Probably build-time, but worth evaluating.
3. **What's the exact checksum scope per provider?** OpenSearch migration body is `statements.json`; Postgres is the SQL file; Aerospike/Couchbase/MongoDB use parsed statements. Need a uniform contract that hashes "the bytes that drive UpAsync."
4. **How does `Replaces=[…]` interact with profiles and `[Migration].Profiles`?** A rollup with `Profiles=["prod"]` replacing migrations with mixed profiles is ambiguous — refuse, or fuse only same-profile?
5. **Is there a need for `Replaces=` to span multiple **migration assemblies** (cross-package)?** Django squash is per-app; cross-app squashing is fragile. Probably scope to single-assembly initially.
6. **Should the runner enforce that an `Replaces=[…]` migration's checksum matches the *expected* checksum of the original chain?** Tighter integrity, but fragile if anyone hand-edits an original migration after rollup is authored.
7. **Aerospike specifically — is rollup tooling worth the investment given how thin the schema is?** May be cleaner to document hand-authored baselines as the supported pattern for Aerospike and not build snapshot tooling.
8. **What does CI/CD look like for rollup commits?** A rollup commit deletes files and adds a new one; PR review needs to assert "the round-trip verification passed" — does this need a CI check, a commit message convention, or a separate audit log?

---

## Recommended Next Step

**`/nop:propose`** with this research as input. Rollups are a multi-approach decision (Phase 1 alone has open questions about attribute shape and checksum scope; Phase 2 has a fusion-rule design space; Phase 3 has per-provider introspection options). The propose skill's evolutionary selection across fitness dimensions (correctness, mechanism-design, performance, ADR compatibility) is the right tool to land the design.

A possible second pass after `propose` lands the architecture: **`/nop:adr`** to record the chosen rollup contract (likely a multi-ADR set: one for the `Replaces=` mechanism, one for the per-provider strategy contract, one for Down-of-rollup being unsupported).

`/nop:plan` then breaks Phase 1 into vertical slices for `/nop:implement`.

---

## References

### External — Tool Documentation
- [Django Migrations Topics — squashmigrations](https://docs.djangoproject.com/en/5.1/topics/migrations/#squashing-migrations)
- [EF Core Managing Migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing)
- [dotnet/efcore #2174 — squash migrations](https://github.com/dotnet/efcore/issues/2174)
- [dotnet/efcore #33118 — consolidate migrations](https://github.com/dotnet/efcore/issues/33118)
- [Squashing EF Core Migrations Safely (Mitchel Sellers)](https://mitchelsellers.com/blog/article/squashing-ef-core-migrations-safely)
- [Cleaning Migrations in EF Core (codewithmukesh)](https://codewithmukesh.com/blog/cleaning-migrations-efcore/)
- [Rails Active Record Migrations Guide](https://guides.rubyonrails.org/active_record_migrations.html)
- [`squasher` gem (Rails)](https://github.com/jalkoby/squasher)
- [Flyway Baselines and Consolidations (Redgate)](https://www.red-gate.com/hub/product-learning/flyway/flyway-baselines-and-consolidations)
- [Flyway Baseline Migrations Explained (Redgate)](https://www.red-gate.com/hub/product-learning/flyway/flyways-baseline-migrations-explained-simply)
- [Flyway Repeatable Migrations](https://documentation.red-gate.com/fd/repeatable-migrations-273973335.html)
- [flyway/flyway #470](https://github.com/flyway/flyway/issues/470)
- [Liquibase generateChangelog](https://docs.liquibase.com/commands/inspection/generate-changelog.html)
- [Liquibase diff-changelog](https://docs.liquibase.com/commands/inspection/diff-changelog.html)
- [Liquibase Diff Best Practices (blog)](https://www.liquibase.com/blog/liquibase-diffs)
- [Prisma Squashing Migrations](https://www.prisma.io/docs/orm/prisma-migrate/workflows/squashing-migrations)
- [Prisma Baselining](https://www.prisma.io/docs/orm/prisma-migrate/workflows/baselining)
- [Prisma migrate resolve](https://www.prisma.io/docs/cli/migrate/resolve)
- [Sqitch Rework](https://sqitch.org/docs/manual/sqitch-rework/)
- [Knex Migrations Guide](https://knexjs.org/guide/migrations.html)
- [knex/knex #2728 — squashing](https://github.com/knex/knex/issues/2728)
- [adonis-lucid-migration-squash](https://github.com/the-alien-club/adonis-lucid-migration-squash)
- [Atlas Declarative vs Versioned](https://atlasgo.io/concepts/declarative-vs-versioned)
- [Atlas migrate diff](https://atlasgo.io/versioned/diff)
- [Atlas vs Classic Tools](https://atlasgo.io/atlas-vs-others)

### External — Academic
- [Curino, Moon, Zaniolo — PRISM (VLDB 2008)](https://www.vldb.org/pvldb/vol1/1453939.pdf)
- [Curino — Automating Database Schema Evolution (VLDB Journal 2013)](https://www.curino.us/wordpress/?p=160)
- [Herrmann, Ho, Märtin, Lehner — Living in Parallel Realities (SIGMOD 2017)](https://dl.acm.org/doi/10.1145/3035918.3064046)

### Internal — Hyperbee.Migrations Codebase
- [`MigrationRunner.cs`](../../src/Hyperbee.Migrations/MigrationRunner.cs) — version ordering and discovery
- [`IMigrationRecordStore.cs`](../../src/Hyperbee.Migrations/IMigrationRecordStore.cs) — ledger contract
- [`MigrationRecord.cs`](../../src/Hyperbee.Migrations/MigrationRecord.cs) — current record shape
- [`MigrationAttribute.cs`](../../src/Hyperbee.Migrations/MigrationAttribute.cs) — version + profile metadata
- [`Migration.cs`](../../src/Hyperbee.Migrations/Migration.cs) — Up/Down contract
- [`OpenSearchStatementParser.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Grammar/OpenSearchStatementParser.cs) — parser for fusion candidate
- [`Internal/Ast/`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Ast/) — 21 AST types
- [`StatementDispatcher.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Dispatch/StatementDispatcher.cs) — verb routing
- [`OpenSearchExceptions.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchExceptions.cs) — partial-rollback infrastructure
- [`tests/.../Container/`](../../tests/Hyperbee.Migrations.Integration.Tests/Container/) — Testcontainers per-provider wrappers

### Internal — Related ADRs
- [ADR-0003 — Provider Record Store Contract](../decisions/0003-provider-record-store-contract.md) — anchor for ledger schema changes
- [ADR-0009 — Convention-Based Record IDs](../decisions/0009-convention-based-record-ids.md) — anchor for `Replaces=` ID resolution
- [ADR-0011 — Hybrid Parser Runtime Injection](../decisions/0011-hybrid-parser-runtime-injection.md) — anchor for OpenSearch fusion middleware placement
- [ADR-0014 — State Machine Facade Over Pipeline](../decisions/0014-state-machine-facade-over-pipeline.md) — pattern for rollup workflow if multi-step
- [ADR-0015 — Parser Offline Pure, All I/O Runtime](../decisions/0015-parser-offline-pure-all-io-runtime.md) — constrains where snapshot capture can run
