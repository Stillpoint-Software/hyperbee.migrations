# ADR-0022: Script-Format Resource Migrations (Cross-Provider)

**Status:** Accepted
**Date:** 2026-05-05
**Amendments:** A1 (2026-05-06) — Postgres dollar-quote authoring rules added per spike finding F6 (`spikes/postgres-classifier/SPIKE_REPORT.md`).
**Related design:** [docs/design/migration-squashing.md](../design/migration-squashing.md), [docs/design/migration-squashing-consensus-destructive.md](../design/migration-squashing-consensus-destructive.md)
**Related ADRs:** ADR-0001 (Parlot), ADR-0002 (Resource Migration Pattern), ADR-0009 (Convention-Based Record IDs), ADR-0017 (Body-Source Grammar), ADR-0019 (Squash via Replaces Graph), ADR-0021 (Migration Record Checksum)

## Context

Resource-based migrations across the four NoSQL providers (Aerospike, Couchbase, MongoDB, OpenSearch) currently use a JSON-array container shape established by ADR-0002:

```json
{
  "statements": [
    { "comment": "Component template", "statement": "CREATE COMPONENT mappings_common WITH BODY @mappings_common.json" },
    { "comment": "Index template", "statement": "CREATE TEMPLATE logs_v2 WITH BODY @logs_v2_template.json" },
    { "comment": "Health barrier", "statement": "WAIT FOR HEALTH GREEN" }
  ]
}
```

Postgres uses raw `.sql` files via `PostgresResourceRunner.AllSqlFromAsync` — script form natively. This asymmetry means **the four NoSQL providers fight JSON-as-container around content that's already a parseable mini-language**. Authors maintain JSON quoting/escaping ceremony; reviewers diff JSON noise instead of intent; squash codegen emits structured-array output that's harder to scan than equivalent script form.

Since each provider already has a Parlot grammar for its statement language (per ADR-0001), the parser is already statement-by-statement; the JSON-array container is purely a delivery mechanism. **Lifting parser entry to a script-level rule is small grammar work** — multi-statement parsing, top-level comment handling, statement terminator recognition.

The destructive-model squash design (ADR-0019 + Assessment 0007) introduces a generation determinism gate (C12) that requires byte-stable codegen output across re-runs. JSON has well-defined canonicalization rules (sort keys, normalize whitespace, etc.); script form requires explicit canonical-formatter rules but is no harder in principle.

A unified script format also closes the asymmetry between Postgres (`.sql`) and the four NoSQL providers (`.statements.json`). Operators reach for a familiar mental model — write a SQL-like script — regardless of provider.

## Decision

We adopt a **universal script format for resource-based migrations across all five providers**, with backward-compatible support for the existing JSON-array form during a transition window.

### Universal script format syntax

```
-- Line comment (SQL convention)
// Line comment (C/JavaScript convention; Mongo-shell-friendly)
/* Block comment
   spanning multiple lines */

CREATE TABLE users (id bigint PRIMARY KEY, email text NOT NULL);
CREATE INDEX idx_users_email ON users(email);

/* Multi-line statement bodies are fine — semicolon terminates */
CREATE INDEX foo WITH BODY {
  "settings": { "number_of_shards": 1 },
  "mappings": { "dynamic": "strict" }
};

CREATE INDEX bar WITH BODY @bodies/bar.json;
```

**Lexical rules:**

1. **Statement terminator: `;`** — required. Statements without a terminator are a parse error. Universal SQL/N1QL convention.
2. **Comments:** three styles supported uniformly:
   - `--` line comment (SQL)
   - `//` line comment (C/JS/Mongo-shell)
   - `/* ... */` block comment (universal)

   Multiple comment styles are intentional — different provider audiences reach for different conventions; no need to pick.
3. **Whitespace** is free in source: any combination of spaces, tabs, newlines is permitted between tokens. Authors choose their indentation/blank-line patterns.
4. **String literals** — provider-specific (single/double quotes per native grammar; backticks where the provider's grammar uses them).
5. **Embedded JSON bodies** — recognized as brace-balanced consumption respecting string-literal escaping. Per ADR-0017's existing rules; the script-form parser delegates to the existing body-source rules (Form 1/2/3 unchanged).

### Statement parsing

Each provider's existing Parlot grammar already parses individual statements. The script-format change adds a top-level rule:

```
script ::= (comment | statement ';' | whitespace)* EOF
```

The grammar:
1. Consumes leading whitespace/comments
2. Matches a statement (delegating to the existing per-provider grammar)
3. Requires `;`
4. Repeats

Comments are discarded (preserved only in the lossless source form for the squash codegen's `Squash_M.summary.md` artifact, which does not round-trip through parsing). Per-statement `comment:` fields from the JSON-array form become **adjacent script comments** at parse time:

```json
// JSON form
{ "comment": "Component template", "statement": "CREATE COMPONENT foo" }
```

```sql
-- Component template
CREATE COMPONENT foo;
```

Both forms produce the same AST.

### Embedded JSON body parsing

The existing ADR-0017 body-source forms (Form 1: `@path`, Form 2: inline `bodies.<name>`, Form 3: file-pointing `bodies.<name>`) are preserved. The script form adds:

- **Form 1 (`WITH BODY @path`):** unchanged. Authors recommended to use this for non-trivial bodies.
- **Form 4 (NEW — inline brace-balanced):** `WITH BODY { ... }` directly inline in the script. Brace-balanced parsing respects string-literal escaping. Discouraged for bodies >20 lines (style guidance, not enforced).
- **Form 2/3 (`WITH BODY $name`)** — the `bodies.<name>` named-section moves to a **header block** at the top of the script:

  ```sql
  BODIES {
    logs_v2_template: @./logs_v2_template.json
    inline_body: {
      "settings": { "number_of_shards": 1 }
    }
  }

  CREATE TEMPLATE logs_v2 WITH BODY $logs_v2_template;
  CREATE INDEX foo WITH BODY $inline_body;
  ```

  The `BODIES { ... }` header is optional; absent it, only Form 1 (`@path`) and Form 4 (inline `{...}`) are usable.

The legacy JSON-sibling-property body (the back-compat "Form 0" from ADR-0017) is preserved for JSON-array-form migrations only. New script-form migrations cannot use it; the BODIES header replaces it cleanly.

### File extensions and resource discovery

| Provider | Legacy extension | New extension | Rationale |
|---|---|---|---|
| Aerospike | `*.statements.json` | `*.statements` | Cross-provider symmetric |
| Couchbase | `*.statements.json` | `*.statements` | Cross-provider symmetric |
| MongoDB | `*.statements.json` | `*.statements` | Cross-provider symmetric |
| OpenSearch | `*.statements.json` | `*.statements` | Cross-provider symmetric |
| Postgres | `*.sql` | `*.sql` (unchanged) **AND** `*.statements` | Postgres native already script form; `.sql` continues to work |

The resource loader detects format by extension:

- `*.statements.json` → JSON-array loader (legacy)
- `*.statements` → script loader (new)
- `*.sql` → script loader (Postgres native; semantics already match)

Both loaders produce the same AST stream into the dispatcher. Single-pass detection at resource-iteration time; no per-statement dispatch on format.

### Backward compatibility

**The legacy JSON-array form is supported indefinitely.** New work prefers the script form; existing migrations don't need to be migrated.

| Concern | Resolution |
|---|---|
| Existing samples (`runners/samples/.../1000-*.statements.json`) | Continue to work. Migrate lazily as samples are touched for other reasons. |
| Existing customer migrations | Continue to work. Customers update at their pace. |
| ADR-0002 (Resource Migration Pattern) | Amended (see "Relation to other ADRs"); the JSON-array form is no longer the only option but remains valid. |
| ADR-0017 (Body-Source Grammar) | Amended; Form 0 (sibling-property) deprecated for new work but preserved for JSON-array form migrations. |

**Squash CLI codegen emits script form.** Going forward, generated squashes use the new format regardless of source migrations' format. The C12 generation determinism gate (per ADR-0019 amendment A16) tests byte-stable script output.

### Codegen canonical formatter (per provider)

The squash CLI's emitter applies these canonicalization rules to ensure C12 byte-stability:

1. **One statement per logical block.** Single-line statements on their own line. Multi-line statement bodies (e.g., embedded JSON) preserved as-is for human readability with internal canonicalization (sorted JSON keys, single-space token separation in the wrapping statement).
2. **Single space between tokens.** No tab indentation; LF line endings; UTF-8 no BOM.
3. **Explicit `;` terminators.**
4. **Comments preserved verbatim** from author intent when present in the source migrations; squash codegen adds top-of-file banner comments (range, generation timestamp, topology, canonicalizer-version) per the existing `Squash_M.summary.md` artifact convention.
5. **Modifier ordering for commutative clauses** is alphabetical/positional consistently. E.g., `CREATE INDEX foo IF NOT EXISTS WAIT` — the modifier order is grammar-fixed, not author-controlled.
6. **Embedded JSON bodies** canonicalized per the body's native rules (sort properties, sort required, normalize `bsonType`, etc. — already specified per provider in the consensus's per-provider canonicalization sections).

Each provider's canonical-formatter is part of `ISnapshotCanonicalizer` (per consensus C2/C12) — the same canonicalizer that drives byte-stable snapshots also drives byte-stable script emission.

### Per-provider notes

- **Postgres** (v1): already uses `.sql` files. The script-format adoption is a *terminology unification* — Postgres has been doing this all along. Any Postgres-specific lexer additions (e.g., `--` is Postgres-native) carry no friction. **Dollar-quote authoring rules apply** — see "Authoring rules: Postgres dollar-quoted bodies" below.
- **Aerospike** (v1.1): `.statements` script form replaces `.statements.json`. AQL subset already statement-by-statement; tiny grammar lift.
- **Couchbase** (v1.2): N1QL is SQL-shaped; `--` line comments already conventional. `.statements` files; `BODIES` header for shared inline bodies.
- **MongoDB** (v1.1): Mongo-shell-like statements (`db.col.createIndex(...)`) terminate with `;` natively in the shell. `//` line comments are Mongo-shell convention. Both `--` and `//` supported.
- **OpenSearch** (v1.2): the existing 21-statement AST already parses individual statements; lift to script-level adds top-level comment + terminator rules. The `BODIES` header is most useful here (richest body-source usage).

### Authoring rules: Postgres dollar-quoted bodies

Postgres function/procedure bodies are wrapped in dollar-quoted string literals (`$tag$ ... $tag$`). The Postgres lexer treats the body as **opaque bytes** — it does not recognize comment syntax, string-literal escaping, or any other token boundary inside the body. Only the matching close-tag terminates the literal.

This makes function bodies more fragile than they look. Surfaced by the Postgres classifier spike (`spikes/postgres-classifier/SPIKE_REPORT.md` finding F6) after three failed fixture iterations.

**Three rules authors must follow when writing Postgres functions in `.statements` or `.sql` script-form resources:**

1. **The outer dollar-tag must NOT appear anywhere in the body — including inside what looks like a comment.** Postgres' lexer doesn't see comments inside the body; the bytes `$body$` (or whatever tag is in use) close the literal regardless of surrounding context.

   ```sql
   -- WRONG: outer tag $body$ appears inside a body comment, terminating the literal
   CREATE FUNCTION x() RETURNS void LANGUAGE plpgsql AS $body$
   BEGIN
       -- this body uses $body$ as the outer tag      <-- closes the literal HERE
       RAISE NOTICE 'never reached';
   END;
   $body$;
   ```

2. **Plain `$$` outer tag is unsafe if the body contains the substring `$$` anywhere — even inside a string literal or a `--` comment within the body.** Use a tagged outer quote (`$body$`, `$fn_x$`) when the body contains `$$`, and ensure rule 1 still holds.

   ```sql
   -- WRONG: '$$' inside a body comment terminates the outer $$ literal
   CREATE FUNCTION y() RETURNS void LANGUAGE plpgsql AS $$
   BEGIN
       -- handles '$$' substrings                     <-- closes the outer $$ HERE
       RAISE NOTICE 'broken';
   END;
   $$;
   ```

3. **Best practice: choose a unique, function-specific outer tag that doesn't collide with body content.** Conventions like `$fn_<function_name>$` (e.g., `$fn_audit_trg$`) make collision essentially impossible and make the body's intent obvious to reviewers.

   ```sql
   -- CORRECT
   CREATE FUNCTION app.dynamic_query(tbl text) RETURNS SETOF record
   LANGUAGE plpgsql AS $fn_dynamic_query$
   BEGIN
       -- body can freely contain $$, --, etc. with no risk
       RETURN QUERY EXECUTE format($$SELECT * FROM %I$$, tbl);
   END;
   $fn_dynamic_query$;
   ```

**Codegen behavior:** the squash codegen's canonical formatter normalizes function-body outer tags to a deterministic form (pg_dump itself emits `$_$`). This affects byte-stable codegen but does not relax the authoring rules — author-written bodies must follow rules 1-3 to be parseable by Postgres in the first place.

These rules apply only to Postgres. The four NoSQL providers don't have dollar-quoted bodies in their grammars; their script-form authoring is governed by the per-provider Parlot grammars (per ADR-0001).

## Consequences

### Positive

- **Author ergonomics materially improved.** No JSON-string escaping for content that's already a parseable mini-language. Comments are first-class, not bound to a specific next-statement. Statements can be rearranged freely. Multi-line embedded JSON bodies don't fight JSON-quoting.
- **Reviewer experience materially improved.** PR diffs of script files are far more readable than diffs of JSON-encoded statements. Code review surface shrinks; comprehension goes up.
- **Cross-provider symmetry restored.** All five providers now use script-form resources (`.sql` for Postgres, `.statements` for the four NoSQL providers). One mental model. Tooling investments (syntax highlighters, LSP, formatter) target one shape.
- **Tooling can target the actual mini-language.** A future hyperbee LSP could parse `.statements` files directly, providing autocompletion, diagnostics, and rename refactoring on real grammar tokens — not on JSON-string contents.
- **Squash codegen output is more reviewable.** The `Squash_M.statements` artifact is a script that PR reviewers can read top-to-bottom; the C13a summary artifact (`Squash_M.summary.md`) carries the diff narrative. Together they're substantially more useful than a JSON-array dump.
- **Grammar reuse with Postgres `.sql`.** Postgres's existing `.sql` resource pattern aligns with the new universal shape; Postgres uplifts to `.statements` discovery as a "free" extension change without migrating its native script form.

### Negative

- **Two parser entry points per provider** during the transition window (JSON-array loader and script loader). The grammars are shared after entry; the loader detection by extension is one-shot per resource. Modest code complexity.
- **`BODIES` header form is a third body-source mechanism** alongside Form 1 (`@path`) and Form 4 (inline `{...}`). Authors must understand which to reach for. Style guidance: Form 1 (`@path`) for non-trivial bodies; Form 4 (inline) for trivial; `BODIES` header for cross-statement reuse.
- **Codegen canonical-formatter is per-provider work.** Each provider's canonicalizer must implement script emission alongside the existing snapshot canonicalization. Tractable but real implementation cost (per-provider, ~a day each).
- **Comment styles fragment.** Three comment syntaxes (`--`, `//`, `/* */`) supported uniformly mean a single project's migrations may mix conventions. Style guidance is "pick one and stick with it"; lint can enforce per-project.
- **Existing samples and customer migrations stay JSON-array** until lazily migrated. Two formats coexist indefinitely; documentation must address both.

### Neutral

- **The verbs / AST / dispatcher / runtime middleware are unchanged.** This is purely a parser-input format addition. Squash codegen, fleet readiness, verification, classifier, all unchanged structurally.
- **Body-source resolution via `@path` is unchanged.** ADR-0017 Forms 1/2/3 stay; Form 4 (inline brace-balanced) and `BODIES` header are additions, not replacements.
- **Postgres adoption is symbolic.** The provider was already doing script form via `.sql`; the only change is treating `.sql` and `.statements` as equivalent for resource discovery.

## Alternatives Considered

- **Keep JSON-array exclusive; do not add script form.** Rejected — the JSON ceremony is the dominant ergonomics complaint from authors; the verbs are already statement-by-statement parseable; the change is small. Maintaining JSON-only is paying ongoing ergonomics tax to avoid one-time grammar work.
- **Per-provider native extensions** (Postgres `.sql`, OpenSearch `.os`, Couchbase `.n1ql`, etc.). Rejected — provider-native extensions have ecosystem familiarity but break cross-provider symmetry and complicate documentation. `.statements` is uniform; provider dispatch happens by registered handler, not by extension.
- **Force migration of all existing JSON-array files at v1.** Rejected — backward compatibility is cheap (one extra loader path per provider) and avoids a customer-facing migration that adds no functional value beyond format change. Lazy migration is operator-paced.
- **Adopt only one comment style** (e.g., `--` only). Rejected — different audiences reach for different conventions; supporting all three is grammar-trivial in Parlot and removes a friction point. Style consistency is a per-project lint concern, not a framework concern.

## Relation to other ADRs

- **ADR-0001 (Parlot for Statement Parsers):** unchanged. The script-form parser is a lift of the existing per-provider Parlot grammars to a multi-statement entry rule. No new parser library; no new grammar engine.
- **ADR-0002 (Resource Migration Pattern):** **amended.** The pattern is no longer JSON-array-exclusive; script form is added as a co-equal supported format. The `*ResourceRunner` types gain script-form detection at resource-iteration time.
- **ADR-0009 (Convention-Based Record IDs):** unchanged. Migration record IDs derive from class metadata (Version + name); resource format is irrelevant.
- **ADR-0017 (Body-Source Grammar):** **amended.** Form 0 (top-level sibling property) is preserved for JSON-array-form migrations only — deprecated for new work. Forms 1/2/3 unchanged. Form 4 (inline brace-balanced body) and the `BODIES` header convention are added.
- **ADR-0019 (Migration Squash):** consumes this ADR. Squash codegen emits script form (per A19 amendment); C12 generation determinism gate tests byte-stable script output via per-provider canonical formatters.
- **ADR-0021 (Migration Record Checksum):** unchanged. Checksum is over the resource bytes regardless of format; script-form bytes are hashed identically to JSON-array bytes. Per-provider canonicalization rules (per consensus) apply to whichever format is in use.

## Implementation

Per-provider implementation work (rough sizing):

| Provider | Lift | Notes |
|---|---|---|
| Postgres | ~0.5 days | Already script form; add `.statements` extension recognition (alias of `.sql`); confirm canonical formatter |
| Aerospike | ~1-2 days | Lift AQL grammar to multi-statement entry; add comment + terminator rules; canonical formatter |
| Couchbase | ~2-3 days | N1QL has the most complex grammar of the four NoSQL providers; same lift as Aerospike but more rules |
| MongoDB | ~2 days | Mongo-shell-like grammar; `;` and `//` already natural |
| OpenSearch | ~2-3 days | 21-statement AST is the richest grammar; `BODIES` header most useful here; canonical formatter must handle painless script bodies |
| Cross-cutting | ~2 days | Resource loader detection by extension; format-detection unit tests; documentation updates to ADR-0002, ADR-0017, the operator guide |

**Total: ~10-14 days across all five providers.** Independently shippable per provider; the universal scaffolding (resource loader detection) ships first, then per-provider grammar lifts.

## References

- ADR-0001 (Parlot for Statement Parsers): [docs/decisions/0001-parlot-for-statement-parsers.md](0001-parlot-for-statement-parsers.md)
- ADR-0002 (Resource Migration Pattern): [docs/decisions/0002-resource-migration-pattern.md](0002-resource-migration-pattern.md)
- ADR-0017 (Body-Source Grammar): [docs/decisions/0017-body-source-grammar.md](0017-body-source-grammar.md)
- ADR-0019 (Migration Squash via Replaces Graph + Destructive Codegen): [docs/decisions/0019-migration-squash-replaces-graph.md](0019-migration-squash-replaces-graph.md)
- ADR-0021 (Migration Record Checksum): [docs/decisions/0021-migration-record-checksum.md](0021-migration-record-checksum.md)
- Squash design: [docs/design/migration-squashing.md](../design/migration-squashing.md)
- Multi-advocate consensus: [docs/design/migration-squashing-consensus-destructive.md](../design/migration-squashing-consensus-destructive.md)
- Assessment 0007: [docs/research/0007-migration-squashing-destructive-assessment.md](../research/0007-migration-squashing-destructive-assessment.md)
