# ADR-0017: Body-Source Grammar — Three Resolution Forms

**Status:** Accepted
**Date:** 2026-05-02

## Context

The OpenSearch provider's resource format pairs each statement with an
optional JSON body that becomes the request payload. R-09 originally
specified body refs as **sibling properties** on the statement object:

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "usersIndex": { "settings": {...}, "mappings": {...} }
}
```

This shape was load-bearing for early Phase-1 development — atomic
versioning, single-file IDE validation, no external file plumbing. After
shipping the v1 verb set and the runner+samples projects, two design
smells surfaced during a maintainer review of the samples:

1. **Heterogeneous statement objects.** A `statements[]` entry mixes
   one well-known field (`statement`) with arbitrary other-named keys
   that the parser interprets. JSON Schema can't usefully describe
   that shape; tooling can't tell which keys are bodies vs. metadata
   vs. typos.

2. **No graceful path for large or reusable bodies.** Production
   OpenSearch index mappings routinely run 200+ lines (multi-language
   analyzers, completion suggesters, nested types, multi-field).
   Production ISM policies (hot/warm/cold/delete with rollover, force-
   merge, allocation requirements) run 100+ lines. Inline-only puts
   that mass into `statements.json`; PR review becomes "find the actual
   change in a sea of mapping JSON." Nothing supports the natural
   "extract to file, reference by name" pattern that
   Couchbase/Aerospike/MongoDB use for *documents* (their analogous
   external-resource concern).

A reviewer questioned the divergence from the house pattern (folder of
JSON files mapping to collections) and flagged the lack of a structured
body section as a smell to fix before more migrations were written
against the original shape. The cost of changing the format grows
quickly with adopter count; only the OpenSearch provider has shipped
and no external consumers exist yet, so this is the cheapest moment to
revisit.

Three forces in tension:

- **Atomic versioning** — statement and body should change together
  (R-09's original rationale).
- **PR review ergonomics** — large bodies belong in their own files so
  diffs are scoped to the actual change.
- **Schema validation** — the resource format should be describable to
  IDE tooling and JSON Schema.

The original sibling-property form satisfies the first force but
nothing else. Replacing it wholesale would break ADR-0009 and force a
migration on hypothetical future consumers. Augmenting it with new
forms that retain the original as a back-compat case satisfies all
three without breaking anything.

## Decision

We will support **three body-source resolution forms**, ranked by
ceremony, all coexisting:

### Form 1 — Direct file reference (least ceremony)

```json
{ "statement": "CREATE INDEX users WITH BODY @bodies/users-mapping.json" }
```

The path is parsed as a `BodyFileRef` AST node. Resolution loads an
embedded resource at the given path **relative to the migration's own
resource folder**. The file must be marked `EmbeddedResource` in the
project's csproj — same convention as `statements.json` itself.

This is the recommended form for any body that would dominate the
`statements.json` file when inlined.

### Form 2 — Named body in the `bodies` section (inline JSON)

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "bodies": {
    "usersIndex": { "settings": {...}, "mappings": {...} }
  }
}
```

The parser produces a `BodyRef("usersIndex")` AST node. Resolution
looks up `bodies.usersIndex` and uses its value verbatim. This is the
recommended form for tiny bodies tightly coupled to a single statement.

### Form 3 — Named body in the `bodies` section pointing at a file

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "bodies": {
    "usersIndex": "@bodies/users-mapping.json"
  }
}
```

When the value of a `bodies.<name>` entry is a string starting with
`@`, the resolver treats it as a path reference and loads the
embedded resource. Use this form when you want to address bodies by
name (e.g., for clarity in PR review) but keep them in their own
files. Rare in practice — form 1 covers the common case.

### Back-compat (form 0) — Top-level sibling property (ADR-0009/R-09)

```json
{
  "statement": "CREATE INDEX users WITH BODY $usersIndex",
  "usersIndex": { "settings": {...} }
}
```

When `bodies.<name>` is missing, the resolver falls back to a
top-level sibling property of the same name. Preserves the
ADR-0009/R-09 shape for migrations written before this ADR. The
fallback is silent — no warning — because the form was the documented
contract; migrating existing resources is optional.

### Resolution order

1. `BodyFileRef` (the `@path` form): load the embedded resource, parse
   as JSON.
2. `BodyRef` with a `bodies` section entry: structured form wins.
3. `BodyRef` with a sibling property: ADR-0009 fallback.
4. None of the above: throw `InvalidOperationException` with a
   remediation message naming both the preferred form and the
   fallback.

### Path validation (parse-time)

The grammar accepts characters `[a-zA-Z0-9_\-./\\:]` in `@path`. The
`:` is in the lexer's accept set only so a drive-letter prefix surfaces
as a clean "absolute path" error rather than a generic parse failure.
Validation rejects at parse time:

- Absolute paths (leading `/` or `\`) — body files must be inside the
  migration's resource folder.
- Drive-letter prefix (`C:`, `c:`, ...) — same reason. `Path.IsPathRooted`
  is platform-dependent so an author editing on one host could otherwise
  produce a manifest that's silently rooted on another.
- Any other `:` in the path — embedded resource names don't use it; the
  reject is mechanical because a `:` that isn't a drive-letter prefix
  is almost certainly an authoring mistake.
- `..` segments — no parent-directory traversal; each migration's
  body files stay self-contained.

Filenames legitimately containing dots (e.g., `users.v2.json`) are not
mistaken for parent-traversal because the validator splits on `/` and
checks each segment.

## Consequences

**Easier:**

- Large bodies live in their own files. PR diffs scope to one concern.
- Schema validation describable: a `bodies` object with named
  values that are either inline JSON or `@`-prefixed strings.
- The most common case (single body, lives in a file) takes one line:
  `WITH BODY @bodies/foo.json`. No `bodies` section needed.
- Authors learning the format see the structured `bodies` section in
  samples first; they discover the back-compat sibling form only when
  inheriting existing migrations.

**Harder:**

- The resolver has more cases to maintain (3 forms + 1 fallback).
  Mitigated by a single `ResolveBody` helper called from both Up and
  Down dispatch paths.
- Authors face a small "which form do I use?" decision per body. The
  README provides clear guidance: small inline → form 2; large or
  reusable → form 1.

**Constrained:**

- Embedded resources only. No filesystem-relative paths, no absolute
  paths, no parent traversal. Keeps `dotnet publish` boundaries
  honest and prevents migration content from depending on runtime
  filesystem layout.
- File extensions are open (`.json` is conventional but not enforced)
  — the file is parsed as JSON regardless of extension.

**Backwards-compatible:**

- ADR-0009/R-09 sibling-property semantics preserved as the silent
  fallback. No existing migration needs to be rewritten.

## Relation to other ADRs

- **ADR-0009 (Convention-Based Record ID Generation)** — unaffected.
  This ADR addresses body-ref resolution, not record IDs.
- **ADR-0011 (Hybrid Parser+Runtime Injection)** — preserved. The
  parser still owns intent (BodyRef vs BodyFileRef discrimination at
  parse time); runtime resolves the reference to a JSON tree.
- **ADR-0015 (Parser is Offline-Pure)** — preserved. Parsing produces
  AST nodes carrying paths/names; no resource loading or filesystem
  access at parse time. Embedded-resource loading is runtime concern.

## Implementation

- `BodySource` abstract base record with two variants: `BodyRef(Name)`
  and `BodyFileRef(Path)`.
- All body-bearing AST records (`CreateIndexAst`, `ReindexAst`,
  `UpdateMappingAst`, `UpdateSettingsAst`, `CreateTemplateAst`,
  `CreateComponentAst`, `CreatePolicyAst`) carry `BodySource? Body`.
- Grammar's `bodyRef` parser is `OneOf(siblingBodyRef, fileBodyRef)`
  with parse-time path validation in the `fileBodyRef` callback.
- `OpenSearchResourceRunner.ResolveBody` is the single resolution
  helper called from both `RunStatementsFromJsonAsync` and
  `RollbackStatementsFromJsonAsync`.
- Sample migrations 1, 2, 5, 6, 7, 8 use form 2; sample 3 uses form 3
  (one body) + form 2 (others); sample 4 uses form 1.
