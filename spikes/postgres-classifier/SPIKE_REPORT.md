# Postgres Statement Classifier — Spike Report

**Date:** 2026-05-04
**Branch:** `devs/bfarmer/provider-squash`
**Plan reference:** `docs/plans/active/migration-squashing-v1.md` Phase 6 Task 6.3
**Status:** Spike complete; recommendations for Phase 6 implementation included.

## Purpose

De-risk Phase 6 Task 6.3 (Postgres `IDataOpClassifier` + statement classifier for the
destructive squash codegen) by building a working prototype against real `pg_dump
--schema-only` output. The plan estimated 600-1000 LOC for this task and flagged it
as the single highest-risk item in the v1 squash work. The spike asks: are the
parsing surfaces what we think they are? what falls through the cracks? does the
600-1000 LOC envelope hold?

## What was built

| Component | LOC | Purpose |
|-----------|-----|---------|
| `PostgresStatementSplitter` | ~150 | Splits a SQL script into top-level statements while respecting `$tag$...$tag$`, `$$...$$`, `'...'` (with `''` escape), `"..."`, `--` line comments, `/* */` block comments (nested). |
| `PostgresStatementClassifier` | ~250 | Pattern-matches the leading keywords of each statement to a typed `ClassifiedStatement` record (29 distinct kinds). |
| Kitchen-sink schema fixture | ~140 | Synthetic Postgres 16 schema covering extensions, custom types, enums, domains, sequences, identity + generated columns, range + list partitioning, RLS, dollar-quoted function bodies (plain + tagged + nested), triggers, views, materialized views, comments. |
| Test harness | ~170 | Testcontainers Postgres 16-alpine. Applies the fixture via `psql`, captures `pg_dump --schema-only` via `docker exec`, runs splitter+classifier, emits a tally. |

Total: **~710 LOC** for the prototype (within the 600-1000 LOC plan estimate).

## Results

### Classification coverage

Against a real `pg_dump 16.13 --schema-only` output of the kitchen-sink schema:

```
Total statements parsed: 69
Known classifications:   61   (88.4%)
Unknown:                  8   (11.6%)
```

The spike's pass threshold was >= 80% known. **Result: 88.4%** on first attempt.

### Tally by kind (sorted by frequency)

```
AlterTable                  12
SetParameter                10
Unknown                      8
CreateTable                  8
CreateIndex                  5
CreateFunction               4
AlterTableAttachPartition    4
Comment                      3
CreateType                   2
CreateSequence               2
SelectPgCatalog              1
CreateSchema                 1
CreateExtension              1
CreateDomain                 1
AlterSequence                1
CreateView                   1
CreateMaterializedView       1
CreateUniqueIndex            1
CreateTrigger                1
AlterTableEnableRls          1
CreatePolicy                 1
```

### Failure modes (the 8 Unknown)

| # | Cause | Count | Severity | Fix scope |
|---|-------|-------|----------|-----------|
| 1 | `\restrict` / `\unrestrict` psql directives in pg_dump 16+ preamble/postamble | 2 | Critical | ~30 LOC: strip `\<directive>` lines before splitting OR add `PsqlDirective` kind |
| 2 | `ALTER INDEX ... ATTACH PARTITION` (partition index attach) | 6 | High | ~30 LOC: add `AlterIndex` regex + kind |

Both failure modes are well-bounded and trivial to address. **No surprises in the 20%.**

## Findings

### F1 — pg_dump 16 emits `\restrict`/`\unrestrict` psql directives

`pg_dump 16.13` wraps schema dumps with psql session-state directives:

```
\restrict 4Pt21ZeOKU1ZacKubBtWsY5Sm0T46qPm54i2P8hWqx6rHb90IILjU4pQE4moOgp
... <dump content> ...
\unrestrict 4Pt21ZeOKU1ZacKubBtWsY5Sm0T46qPm54i2P8hWqx6rHb90IILjU4pQE4moOgp
```

These are not SQL statements — they're psql client commands that protect against
restricted-character injection in the dump. The classifier must filter them OR
recognize them as a `PsqlDirective` kind. Note: this also means the splitter
sees them as part of the surrounding statement (because they don't end with `;`),
which currently causes a leading `SET statement_timeout = 0` to be misclassified.

Recommendation for Phase 6: a lightweight pre-pass that strips `\<word>...\n`
lines from the dump before passing to the splitter. ~30 LOC, single regex.

### F2 — pg_dump emits partition index attachment as a separate `ALTER INDEX`

For partitioned tables, pg_dump emits a `CREATE INDEX` on each partition (or the
parent), then an `ALTER INDEX parent_idx ATTACH PARTITION child_idx` to wire
them. The current classifier only handles `ALTER TABLE`, `ALTER SEQUENCE`,
`ALTER POLICY`. Adding `AlterIndex` (with optional `AttachPartition` detail)
covers it.

### F3 — pg_dump rewrites function body dollar tags to `$_$`

Function bodies in the input fixture used `$$`, `$body$`, `$dq$` outer tags
based on body content. pg_dump normalizes them all to `$_$` regardless of input.
This means the classifier cannot rely on tag identity for fingerprinting —
canonicalization (Phase 6) must hash the tag-stripped body, not the raw bytes.

### F4 — pg_dump emits PRIMARY KEY as a separate `ALTER TABLE ADD CONSTRAINT`

Inline `PRIMARY KEY` and `UNIQUE` constraints in `CREATE TABLE` are extracted by
pg_dump and re-emitted as `ALTER TABLE ... ADD CONSTRAINT ... PRIMARY KEY (...)`.
This is why the tally shows 12 `AlterTable` statements for an 8-table fixture.
For the canonicalizer, this is *desirable* — diffing constraint-by-constraint
is cleaner than diffing inline-vs-extracted.

### F5 — pg_dump SET preamble is stable but provider-version-dependent

The dump opens with ~10 `SET` statements: `statement_timeout`, `lock_timeout`,
`idle_in_transaction_session_timeout`, `client_encoding`,
`standard_conforming_strings`, `xmloption`, `client_min_messages`, `row_security`,
plus a `SELECT pg_catalog.set_config('search_path', '', false)`. This shape is
known to evolve across Postgres major versions (e.g. `transaction_timeout` arrived
in PG17). The canonicalizer should *strip* this preamble before fingerprinting
to keep generation deterministic across server versions.

### F6 — Authoring rule for dollar-quoted function bodies (HIGH-IMPACT)

This finding came from THREE failed iterations of the kitchen-sink fixture. It
must be documented for the script-format resource grammar (ADR-0022) and for
authors writing functions inside `.statements` files:

**Inside a dollar-quoted string, NOTHING but the matching close tag terminates
the literal — not `--`, not `/* */`, not `'`, not `"`. The Postgres lexer treats
the entire body as opaque bytes.**

Practical consequences:

1. The outer dollar-tag must NOT appear anywhere in the body, including inside
   what would otherwise be a comment within plpgsql code. The fixture's
   `dynamic_query` function originally used `$body$` as the outer tag and had a
   `-- this body literally contains $$ which is why we use $body$ as the outer
   tag` comment inside. Postgres' lexer doesn't see the comment — it sees
   `$body$` as a literal close marker, terminating the body mid-function.

2. A function whose body uses `$$` outer cannot contain the substring `$$`
   anywhere — even inside a literal string or comment within the body. The
   first fixture iteration had `'$$' inside a string is irrelevant here`
   inside a `--` comment. The lexer terminated the body at that `$$`, leading
   to a syntax error one statement later (where the *real* close `$$` was now
   parsed as opening a new dollar-quote that never closes).

3. Best practice: choose a unique outer tag per function (e.g. `$fn_audit_trg$`,
   `$fn_dynamic_query$`) and ensure it doesn't appear inside the body.

This rule should be called out in `docs/decisions/0022-script-format-resource-migrations.md`
and in the user-facing authoring guidance for the new `.statements` script form.

### F7 — Testcontainers + psql + docker exec is a clean spike harness

The harness pattern (apply via `psql -f` inside the container, dump via
`docker exec pg_dump`) sidesteps Npgsql's client-side multi-statement parser
limitations and gives a faithful round-trip against real Postgres tooling. This
pattern should be reused for Phase 6 Task 6.5 (verification round) — the
verification round needs the same shape: apply on container A, apply on
container B, snapshot both, diff.

## Phase 6 calibration

Original plan estimate: **600-1000 LOC** for Postgres squash codegen including
classifier, canonicalizer, and verification harness.

Spike data refines this:

| Subcomponent | Spike LOC | Production LOC estimate | Confidence |
|--------------|-----------|-------------------------|------------|
| Splitter | 150 | 200 (add psql directive strip; minor edge cases) | High |
| Classifier (DDL kinds) | 250 | 500-700 (full ALTER coverage, DROP shapes, ROLE/POLICY/RULE/EVENT TRIGGER) | Medium |
| `IDataOpClassifier` (DML-in-function-body scan) | 0 | 200-400 (separate concern; not exercised by spike) | Low |
| Canonicalizer (whitespace + tag normalization) | 0 | 100-150 | Medium |
| Verification harness (apply + dump + diff on 2 containers) | 170 (test harness) | 200-300 (production: parallel A/B, container lifecycle per ADR-0019 A18) | Medium |
| **Total** | **570** | **1200-1750** | |

**Recommendation:** revise Phase 6 Task 6.3 estimate from **600-1000 LOC** to
**1200-1750 LOC**. The original estimate underweighted `IDataOpClassifier`
(separate scan over user code, not pg_dump output) and the verification harness
production hardening (parallel A/B + container lifecycle on failure per Assessment
0007 amendment A18). Spike output suggests **~5-7 working days** of focused
implementation rather than the originally implied 4-5.

**Risk update:** Phase 6 Task 6.3 was the highest-risk item in the v1 plan.
After the spike: risk is **moderate, not high**. The parsing surfaces are
well-bounded and the failure modes encountered are all trivial. The remaining
material risks are:

1. `IDataOpClassifier` accuracy on user-defined function bodies that contain
   data ops via `EXECUTE format(...)` (Assessment 0007 finding 5; A8 mandates
   `[DataMigration]` annotation as the load-bearing signal anyway). **Status: mitigated by A5/A8.**

2. Determinism of pg_dump output across Postgres minor versions
   (e.g. `transaction_timeout` arriving in PG17). The canonicalizer's preamble
   strip handles this. **Status: low risk.**

3. Generation determinism gate (C12) — the verification round must catch
   non-deterministic codegen even when the snapshot is byte-identical. This
   spike didn't exercise generation, only classification of an existing dump.
   **Status: deferred to Phase 6 implementation.**

## Recommendations for Phase 6 (concrete)

1. **Phase 6 Task 6.3a** — Splitter: add `\<directive>` strip pre-pass. ~30 LOC.
2. **Phase 6 Task 6.3b** — Classifier: add `AlterIndex` (with `AttachPartition` detail) and `DropX` family (likely needed even though pg_dump doesn't emit DROP, because the squash diff *will* emit DROP statements when objects disappear). ~80 LOC.
3. **Phase 6 Task 6.3c** — Canonicalizer: strip pg_dump preamble (the SET block + `\restrict`/`\unrestrict` + comment headers); normalize function-body dollar tags to a canonical form before hashing. ~100 LOC.
4. **Phase 6 Task 6.3d** — Reuse the spike's Testcontainers + psql + docker exec harness pattern verbatim for the verification round (Phase 6 Task 6.5).
5. **ADR-0022 amendment** — add a "Dollar-quote authoring rules" subsection with finding F6's three-point list. The rule must be stated for users writing functions in `.statements` script-format resource files.
6. **Plan amendment** — revise Phase 6 estimate from 4-5 days to 5-7 days, total v1 from 4-6 weeks to 5-7 weeks.

## Conclusion

The spike confirms that Postgres `pg_dump --schema-only` output is **tractable
to classify** with the parsing surfaces we identified in the plan. No new
high-severity surprises were found. Two minor surfaces (`\restrict` directive,
`ALTER INDEX ATTACH PARTITION`) are trivially addressable. One authoring rule
(F6) needs to be lifted into ADR-0022 documentation. Phase 6 implementation
estimate is revised upward by ~50% on better data, but risk classification
moves from **High** to **Moderate**. **Phase 6 Task 6.3 is greenlit to proceed
in v1.**

## Artifacts

- `spikes/postgres-classifier/Fixtures/kitchen-sink.sql` — synthetic schema input
- `spikes/postgres-classifier/PostgresStatementSplitter.cs` — prototype splitter
- `spikes/postgres-classifier/PostgresStatementClassifier.cs` — prototype classifier
- `spikes/postgres-classifier/ClassifierSpikeTests.cs` — Testcontainers harness
- `spikes/postgres-classifier/bin/Debug/net10.0/captured-dump.sql` — captured pg_dump output (build artifact, not committed)
- `spikes/postgres-classifier/bin/Debug/net10.0/classifier-tally.txt` — full tally (build artifact, not committed)
