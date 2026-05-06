# EF Core Squash Reference - for Hyperbee Provider Advocates

**Date:** 2026-05-05
**Status:** Reference Document (consultant input for hyperbee.migrations squash design)
**Audience:** All 5 provider advocates working on hyperbee.migrations squash design

## Why this document exists

Hyperbee.Migrations is preparing to ship migration squashing across five heterogeneous providers (Aerospike, OpenSearch, MongoDB, Postgres, Couchbase). The proposed model is destructive in the Flyway/Atlas tradition: a codegen tool spins an ephemeral container, applies migrations less than N to produce snapshot A, applies the range [N..M] to produce snapshot B, diffs A against B, emits a single squash migration, and removes the source files for [N..M]. Ledger rows survive forever for audit. A fleet readiness check refuses to squash if any environment is mid-range. Mature environments auto-mark via a `Replaces` graph. Fresh environments run residual migrations less than N then the squash body.

Before five advocates argue about details, they should know what the most-used migrations system on the planet has tried, failed at, and asked for. EF Core's GitHub issue #2174 ("Squash migrations") has been open since 2015. It has 400+ thumbs-up reactions, dozens of duplicates, and a consistent message from the EF team: "we know, it's hard, the model is wrong." This document distills that 11-year conversation, plus the surrounding design choices in EF Core, into concrete guidance for hyperbee's design.

This is not a hyperbee provider proposal. EF Core is the patient, not the candidate.

## EF Core's 11-year squash journey (#2174 and #33118)

Issue #2174 was filed in October 2015, two months after EF Core's first preview. The ask was simple: collapse a long migration list into a single baseline. Eleven years later, it is still open. Issue #33118 (filed 2024) is the team's most recent acknowledgement that the original squash design is impossible to retrofit cleanly.

What the EF team has said over the years:

1. **The blocker is "replaces" memory.** Brice Lambson and Andriy Svyryd have repeatedly said that a real squash needs the new baseline migration to *remember* which old migrations it subsumes, so that a database with any prefix of the old migrations applied can be auto-marked instead of mis-applied. EF's `__EFMigrationsHistory` table is just `(MigrationId nvarchar(150), ProductVersion nvarchar(32))`. There is no slot for "this row was retired by squash X." A migration that has been deleted from disk but is still recorded in the history table simply *appears as orphan*, and the runtime cannot tell whether it was retired by squash or was a typo.

2. **Model snapshot regeneration fights squash.** Every `Add-Migration` regenerates `ModelSnapshot.cs` from the live `DbContext`. If you delete the migrations folder and run `Add-Migration InitialCreate`, the snapshot is rewritten *without provenance*. The new InitialCreate cannot diff itself against anything because nothing remembers the prior shape.

3. **Manual data migrations vanish silently.** `migrationBuilder.Sql("INSERT INTO ...")` calls do not feed back into the model snapshot. A squash regenerated from the snapshot loses every raw SQL statement. The team has acknowledged this in #2174 and called it the "data-migration hole." There is no roadmap fix.

4. **Fleet coordination is an operator concern.** EF has never tried to enforce "all environments at the same point before squash." The community advice ("synchronize all envs first, then squash") is universal but undocumented. EF teams typically enforce it via internal runbook and CI gates that they wrote themselves.

The recurring blocker theme: EF Core's history table and snapshot mechanic were designed for *append-only* migrations, and squash is a *destructive* operation that needs a different metadata shape. The team has not been willing to break the history table format, so #2174 stays open.

## What EF Core gets right

1. **`IDesignTimeDbContextFactory<TContext>` cleanly separates design-time from runtime.** The scaffolding tools (`dotnet ef migrations add`, `Remove-Migration`) construct the `DbContext` via this factory, which can require connection strings, secrets, or container endpoints that runtime code never sees. Hyperbee's squash codegen tool should adopt the same separation: the codegen pipeline runs design-time logic against an ephemeral container; the runtime migration runner never touches that surface area.

2. **Migrations are ordered C# classes with `Up`/`Down`.** Code-only migrations (no DSL, no YAML) age well, support refactoring, and let providers like MongoDB and Aerospike that have no schema language still ship migrations as code. Hyperbee already does this; EF validates the choice.

3. **`ProductVersion` in the history table.** Each row records which EF Core version applied it. This is small but priceless when debugging "why is this database weird" five years later. Hyperbee should keep version provenance in its ledger; the squash codegen should also stamp the squash ledger row with the codegen tool version.

4. **Strict ordering by migration ID.** EF refuses to apply migrations out of order against a database that has skipped one. This catches operator mistakes early. Hyperbee's existing version-based ordering inherits this; squash must preserve it.

## What EF Core gets wrong (anti-patterns)

1. **`__EFMigrationsHistory` schema is too thin.** Two columns (`MigrationId`, `ProductVersion`). No checksum. No "kind" column to distinguish schema vs data vs squash. No `replaces` foreign key. No applied-at timestamp. Every framework that came after EF Core (Flyway, Liquibase, Atlas, Sqitch) adds at least a checksum and an applied-at. The lack of checksum means schema drift detection is impossible from the history table alone - EF cannot tell you "this migration's content changed since it was applied." Hyperbee's ledger should carry checksum, kind, applied-at, and a `replaces` array (or join table) on day one.

2. **Squash today is "delete the folder and pray."** The community-blessed manual workflow:
   - `Remove-Migration` repeatedly until target point
   - Delete the entire `Migrations/` folder
   - `Add-Migration InitialCreate`
   - `TRUNCATE __EFMigrationsHistory` on every environment
   - Pray no environment was mid-range

   This destroys provenance. After it runs, you cannot tell from the database which historical migrations were subsumed; you only know "InitialCreate at version X." If a fleet member was at migration N-3 when the squash happened, the truncate-and-restart step is the only path forward, which means downtime and lost ledger history.

3. **`ModelSnapshot.cs` regenerates on every `Add-Migration`.** It is a derived artifact that is committed to source control and re-derived on each migration. This desyncs trivially: merge two PRs that each added a migration, and the snapshot file conflicts in ways that look textual but mean "two people had different ideas about the model shape." EF's recommendation is "rebase, regenerate, retest." Hyperbee's snapshot artifact (if any) should be either *fully derived and not committed* or *authored and validated*, not the worst-of-both-worlds middle ground EF picked.

4. **Raw SQL is silently dropped on regeneration.** `migrationBuilder.Sql("UPDATE ...")` does not round-trip through the snapshot. A squash that regenerates from the model loses every data migration. EF users have been bitten by this for a decade. The lesson: any squash codegen that "diffs A against B" must also *carry forward* data-migration code, not just schema delta.

5. **No fleet-readiness primitive.** EF does not know about your environments. It cannot say "production is at migration 47, staging is at 52, do not squash through 50." Operators learn this rule the hard way and write their own CI gates. Hyperbee's plan to bake fleet readiness into the squash codegen is exactly the gap EF leaves.

## Lessons every hyperbee provider advocate should consider

### Postgres advocate

EF's Postgres provider (Npgsql.EntityFrameworkCore.PostgreSQL) is mature and widely deployed, but it inherits all of EF's squash limitations. The Postgres-specific irony: `pg_dump --schema-only` produces exactly the kind of authoritative snapshot a squash needs, and EF does not use it. Hyperbee's Postgres provider can do what EF cannot - capture snapshot A and snapshot B as `pg_dump` output, diff them with a real schema-diff tool (Migra, Atlas, or a hand-rolled comparator over `information_schema`), and emit the squash migration as raw SQL with high fidelity. This is a genuine capability EF has never shipped. The trap to avoid: `pg_dump` includes server-version-specific syntax; the codegen container must match production server version.

### OpenSearch advocate

EF Core does not target search engines. There is no precedent here, which is liberating and dangerous. The closest EF analogy is "non-table resources": views, stored procedures, triggers - all of which EF handles awkwardly through `migrationBuilder.Sql`. OpenSearch's index templates, ILM/ISM policies, component templates, and ingest pipelines are all *named, versioned, idempotent resources* with no analog to a row count. The lesson from EF: do not try to model these as table-like things. Treat each resource kind as having its own snapshot operation (GET the current definition, hash it) and its own diff operation (textual or structural). The squash for OpenSearch is more like a Kubernetes resource reconciliation than a Postgres schema diff.

### MongoDB advocate

EF Core has a MongoDB provider (Microsoft.EntityFrameworkCore.MongoDB) but it explicitly does *not* support migrations. The team's stated reason: schemaless storage means the EF migration model has nothing to diff. This is a direct lesson for hyperbee's Mongo provider: code-only `UpAsync` is the realistic shape, because you cannot diff "what indexes and validators exist" without a ground truth declaration, and you cannot diff document shapes at all. The squash codegen for Mongo is therefore a *replay-and-capture-final-state* operation: apply migrations [N..M] in the container, capture the resulting indexes/validators/collections, emit code that creates them. There is no "diff" step; the snapshot itself is the squash output.

### Aerospike advocate

EF Core does not reach Aerospike. The lesson is by absence: hyperbee's Aerospike provider already has a resource model (sets, indexes, UDFs). The squash codegen should lean on that model, not invent a parallel "snapshot format." Capture the resource set before and after; the diff is `ResourceSetB - ResourceSetA`. The migration to date in the existing provider took roughly one day, so the squash extension should be calibrated similarly - not a multi-week port. If the codegen is taking longer than a few days, you are probably building a parallel snapshot system instead of using the resource model that already works.

### Couchbase advocate

EF Core does not reach Couchbase either. Couchbase's resource model (buckets, scopes, collections, primary indexes, GSI indexes, FTS indexes, eventing functions) is richer than Aerospike's but the same principle applies: the existing provider already enumerates these resources for `UpAsync`. The squash codegen captures the resource set at the start of [N..M] and at the end, diffs them, and emits code that produces the end state. The trap EF would warn you about: do not regenerate from a derived artifact. The container that ran the migrations *is* the source of truth for what state the migrations produced; the snapshot is just a serialization of that state.

## Specific patterns hyperbee should adopt

1. **Ledger schema with `Replaces` from day one.** Every ledger row has a `replaces` field (array of subsumed migration IDs) populated when the row is a squash. Mature environments check this on startup: if I have rows for the subsumed migrations, mark the squash as applied without running it. This is what EF #2174 explicitly says they need.

2. **Per-record checksum.** Every ledger row stores a content hash. Drift detection becomes possible. Re-applying a tampered migration is detectable.

3. **Kind column on ledger rows.** `schema | data | squash | seed` lets the runner make different decisions per kind. EF cannot do this because the schema is too thin.

4. **Codegen runs in an ephemeral container, never in-process with runtime.** Same separation EF achieved with `IDesignTimeDbContextFactory`, but stricter: the squash codegen is its own CLI tool, not a runtime feature.

5. **Fleet readiness check is mandatory at squash creation time, not deployment time.** The check queries every environment's ledger and refuses to emit a squash if any environment is mid-range. This is the universal EF community advice, finally enforced by tooling.

6. **Source files for [N..M] are removed at squash creation.** This is destructive but honest. Keeping the originals (Django-style "additive" model) creates a parallel-history problem where two versions of the truth exist on disk. EF's "delete the folder" anti-pattern is destructive *without* the replaces graph; hyperbee gets the destruction right by *also* keeping replaces memory.

## Specific patterns hyperbee should reject

1. **Derived snapshot files committed to source control.** EF's `ModelSnapshot.cs` is the worst-of-both-worlds choice. Either the snapshot is purely derived (then do not commit it; regenerate in CI) or it is authored (then validate it in CI). Do not commit a regenerated artifact and expect humans to merge it.

2. **Truncate-the-history-table on squash.** EF's manual workflow does this and it is the reason squash is unsafe in EF. Hyperbee's plan to preserve ledger rows forever is correct - the rows are audit history, not current state.

3. **Silent loss of data-migration code.** Any "diff A against B" pipeline must explicitly carry forward data-migration statements that were in [N..M]. They are not visible in the snapshot diff. The codegen tool must scan the source files for [N..M] for raw-write operations and embed them in the squash output, or refuse to squash if it cannot prove they are idempotent.

4. **Implicit fleet assumptions.** Do not document "make sure all envs are synchronized" and call it done. EF has done that for 11 years and it does not work. The check must be machine-enforced.

## Open questions only EF Core's history can answer

1. **What does the EF team actually plan to ship for #2174?** The latest comments (2025) suggest a new history table format with a `replaces` column gated on a major version bump. This is what hyperbee should ship from version 1.0 - there is no legacy schema to honor.

2. **Why has no third-party tool filled the gap?** EFPT, EFCore.Tools.Extensions, and others have tried partial squash workflows. None has stuck. The reason is the snapshot+history coupling: any squash tool has to either monkey-patch the snapshot regeneration or accept that data migrations vanish. Hyperbee avoids this by not having a regenerated snapshot at all - the codegen captures real state from a real container.

3. **What do EF refugees expect when they evaluate hyperbee?** Replaces graph (or they will assume squash is unsafe). Per-record checksum (Flyway and Liquibase have it; EF's lack is felt). Honest data-migration handling (if `migrationBuilder.Sql` analog is silently dropped, they will discover it in production). Fleet readiness as a tooling concern, not an operator runbook. If hyperbee ships these on day one, the migration story from EF is "this is what we always wanted." If any one is missing, refugees will recognize the omission immediately.

The 11-year story of EF Core squash is not a story of laziness. It is a story of metadata shape decisions made early that could not be retrofitted. Hyperbee has the rare luxury of designing the metadata shape *for* squash before shipping it. Spend that luxury wisely.
