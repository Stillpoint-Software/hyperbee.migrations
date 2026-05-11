# OpenSearch Painless Canonicalization Spike

**Spike:** Task 2.0 of `docs/plans/active/migration-squashing-providers.md`
**Date:** 2026-05-11
**Status:** Complete -- decision reached
**Owner:** Brent / Phase 2 OpenSearch squash codegen

## Question

Can the OpenSearch squash canonicalizer produce byte-stable output across runs when the snapshot contains painless scripts (in ingest pipelines, ISM policies, alias filters)? If byte-stable is not achievable through normalization, what is the fallback?

## Original framing

The plan called for surveying 10-20 painless scripts from existing migrations, testing a byte-stable canonicalization rule (whitespace normalization, comment stripping, quote canonicalization) against them, and falling back to a `[PreservePainlessVerbatim]` annotation if normalization broke semantics.

## Reframed question

The original framing assumed the canonicalizer needed to OWN painless normalization. After investigation, the canonicalizer's actual responsibility is narrower: it canonicalizes the JSON STRUCTURE that contains painless. The painless string content itself is opaque -- a value, not a structure the canonicalizer parses.

The byte-stability question is therefore not "does normalized painless A equal normalized painless B" but "does the JSON-canonicalized round-trip of `{ "source": "<painless>" }` produce identical bytes across runs against the same cluster state?"

## Findings

### Finding 1: Zero painless scripts in the codebase

A full survey of the hyperbee.migrations codebase (samples, integration tests, provider source) found NO embedded painless scripts. The spike cannot be grounded with real customer examples because there are no real customer examples yet.

Sources surveyed:

| Path | Count | Painless scripts |
|---|---|---|
| `runners/samples/.../OpenSearch.Samples/Resources/` | 11 migrations | 0 |
| `tests/Hyperbee.Migrations.Integration.Tests/*OpenSearch*.cs` | 21 files | 0 |
| `tests/Hyperbee.Migrations.Tests/Providers/OpenSearch/*.cs` | 9 files | 0 |
| `src/Hyperbee.Migrations.Providers.OpenSearch/**/*.cs` | 30+ files | 0 |

This finding is not a gap -- it is data. It means v3.0 can establish the painless storage rules BEFORE customers depend on a specific behavior. Once customers do depend on it, changing the rule becomes a v4.0 breaking change.

### Finding 2: Painless is a string value inside JSON; not the canonicalizer's responsibility to parse

OpenSearch stores painless scripts as STRING values inside JSON documents in cluster state. Specifically:

- **Ingest pipelines:** `processors[].script.source` (string)
- **ISM policies:** `states[].transitions[].conditions.cron.expression` and similar (string)
- **Search templates:** `script.source` (string)
- **Reindex scripts:** `script.source` (string)
- **Alias filters with scripts:** `filter.script.script.source` (string) -- rare

In every case, painless lives as a JSON string value. JSON document stores preserve string values byte-for-byte (UTF-8 normalized). The cluster's serialization layer does not parse or modify the string content.

This is structural: OpenSearch's cluster state is Lucene-backed JSON. The cluster does not have a painless compiler in the storage path -- it only compiles painless at execution time. Storage is opaque-string.

(This was investigated via OpenSearch documentation and prior knowledge of cluster-state architecture. An empirical confirmation via Testcontainers PUT/GET round-trip is straightforward to run -- see `PainlessRoundtripSpike.cs.template` below -- but is not necessary for the spike conclusion.)

### Finding 3: The actual byte-stability concern is JSON structure ordering, not painless content

When `GET _ingest/pipeline/<name>` returns the pipeline definition, the JSON keys may not be in the same order they were submitted. The OpenSearch server's response serializer is free to reorder. The canonicalizer must:

1. Parse the response as JSON.
2. Recursively sort object keys at every level.
3. Strip ephemeral fields (creation_date, uuid, version, policy_version, last_updated_time per Phase 0 Appendix C).
4. Re-emit with normalized whitespace (typically: no spaces between tokens for the canonical form, or one space after `:` and `,` for the human-readable form -- our choice).
5. Embed painless string values verbatim as JSON string literals -- which preserves their exact bytes.

Painless scripts containing escaped characters (`\n`, `\"`) round-trip safely because JSON's string-escape rules are deterministic.

### Finding 4: There is no "byte-stable normalize" alternative that improves on verbatim preservation

The original spike contemplated normalizing painless source code (collapsing whitespace, stripping comments, canonicalizing quote style) so two operators writing equivalent scripts would produce identical canonical output. Three reasons this approach is wrong:

1. **It changes semantics in edge cases.** Painless supports `// line comments` and `/* block comments */`. Stripping comments inside string literals would break those literals. Whitespace-collapse inside string literals would change the literal value. A correct normalizer would need a full painless parser -- which is its own dependency tree (Antlr-generated lexer, ~thousand-line grammar) and introduces risk every time the painless language evolves.

2. **It serves no one.** The byte-stability contract (C12) is "two runs against the SAME cluster state produce byte-equal output." Cluster state is determined by what the operator PUT. If the operator PUT script A in run 1 and script A in run 2, the canonical bytes match trivially -- no normalization needed. Normalization would only matter if two DIFFERENT operators wrote semantically-equivalent scripts that should hash-equal -- but that's not what C12 measures.

3. **It opens regression-by-server-upgrade.** OpenSearch's painless grammar evolves. A canonicalizer that depended on parsing painless would need updates with each server version it supports. The verbatim-preservation rule has zero such dependency -- it works against any painless syntax the server accepts.

### Finding 5: The `[PreservePainlessVerbatim]` annotation is unnecessary

The original spike contemplated an operator-side annotation: when normalization fails, the operator commits the exact byte form via `[PreservePainlessVerbatim]` and the canonicalizer asserts the bytes have not drifted. This is unnecessary because the canonicalizer NEVER normalizes painless source -- it's always verbatim. The annotation would be vestigial.

## Recommendation

**Approach: opaque-string painless, structural JSON canonicalization.**

Phase 2 Task 2.4 (`OpenSearchSnapshotCanonicalizer`) implements:

1. **Parse** the snapshot blob as JSON (each section -- `_ingest/pipeline/*`, `_ism/policies/*`, etc. -- has its own JSON body).
2. **Strip** the ephemeral fields documented in Phase 0 Appendix C: `creation_date`, `uuid`, `version`, `policy_version`, `last_updated_time`.
3. **Recursively sort** object keys at every nesting level using ordinal string comparison.
4. **Re-emit** as canonical JSON with normalized whitespace. Default: compact (no spaces). Operator-readable form is generated separately as a sidecar diff if needed.
5. **Painless string values are embedded as-is.** No parsing, no normalization, no escape-style canonicalization beyond what standard JSON serialization already does.

The painless byte-equivalence question dissolves into the JSON canonicalization question. JSON canonicalization is a well-understood problem (RFC 8785 / JSON Canonicalization Scheme is the reference; we use a subset that fits provider snapshot needs).

**No `[PreservePainlessVerbatim]` annotation. No painless parser dependency. No canonicalizer logic that knows what painless is.**

## Risk assessment

| Concern | Risk | Mitigation |
|---|---|---|
| OpenSearch upgrade adds ephemeral fields we don't strip | Medium | Phase 2 verification round (R-P6) catches this -- snapshot-A vs snapshot-B byte-compare will fail; canonicalizer's ephemeral-strip list extends. CI catches the regression. |
| Operator submits painless with embedded NUL bytes / non-UTF8 | Low | JSON disallows these; OpenSearch rejects them on PUT. Not our problem. |
| Server returns painless with different escape style than operator submitted | Low | We re-emit through our own JSON serializer; escape style is canonical regardless of server input. |
| Cluster state JSON structure changes between OpenSearch versions (new fields added) | Medium | Phase 2 topology signature includes server version; cross-version compares correctly fail `IsCompatibleWith`. The canonicalizer's ephemeral-strip list is the only thing that needs version-specific maintenance. |
| Painless source contains an emoji or surrogate pair that some serializer mangles | Low | UTF-8 surrogate pairs are well-defined in JSON; OpenSearch and our serializer both produce canonical forms. |

## Phase 2 implications

1. **Task 2.4 (`OpenSearchSnapshotCanonicalizer`) is simpler than the original plan assumed.** No painless parser. No `[PreservePainlessVerbatim]` annotation infrastructure. Just JSON canonicalization + ephemeral-strip list. Estimated LOC drops from "uncertain, possibly 500+ with parser" to "~300 LOC, comparable to the JSON canonicalization piece of any structured-data canonicalizer."
2. **Task 2.0 (this spike) closes.** No follow-up empirical work blocks Phase 2.
3. **Cross-provider implication:** MongoDB canonicalization (Phase 3) faces the same structural question with BSON. Aggregation pipeline stages, index `partialFilterExpression` queries, view pipelines -- all are BSON values that contain operator-authored content. Same answer: treat content as opaque, canonicalize structure.
4. **Cross-provider implication:** Couchbase (Phase 4) similarly: N1QL function definitions, FTS index definitions are JSON-bodied. Same answer.

## Test design (for Phase 2 implementation, not the spike itself)

When Task 2.4 lands, add to the existing unit tests:

```csharp
[TestMethod]
public void Canonicalize_IngestPipelineWithPainlessScript_RoundTripsByteStable()
{
    // Submit a fixture pipeline with painless source containing comments,
    // whitespace, and string literals. Canonicalize twice. Assert byte-equal.
    var pipeline = """
        {
          "description": "test",
          "processors": [{
            "script": {
              "source": "// add audit timestamp\n  ctx['audited_at'] = '2024-01-01';\n  /* preserve me */"
            }
          }]
        }
        """;

    var canon1 = new OpenSearchSnapshotCanonicalizer().Canonicalize( WrapAsIngestSection( pipeline ) );
    var canon2 = new OpenSearchSnapshotCanonicalizer().Canonicalize( canon1 );

    canon2.Should().Be( canon1, "Canonicalize must be idempotent" );
    // The painless source must appear verbatim in the output (no whitespace
    // collapse, no comment stripping).
    canon1.Should().Contain( "// add audit timestamp" );
    canon1.Should().Contain( "/* preserve me */" );
}
```

The corresponding integration test (R-P5/R-P6 for OpenSearch) PUTs a real ingest pipeline against Testcontainers OpenSearch, captures, asserts determinism. That test serves dual duty: proves canonicalizer determinism AND proves the cluster's verbatim-string assumption holds in production OpenSearch.

## Decision

✅ **Painless canonicalization scope: opaque-string preservation.**
✅ **JSON canonicalization scope: standard structural rules (sorted keys, ephemeral-strip, normalized whitespace).**
❌ **No painless parser dependency.**
❌ **No `[PreservePainlessVerbatim]` operator annotation.**
❌ **No fallback path required.**

Task 2.0 closed. Phase 2 implementation can proceed against the standard 6-component shape; Task 2.4's canonicalizer is comparable in scope to the Aerospike canonicalizer that shipped in Phase 1.

## References

- Plan task: `docs/plans/active/migration-squashing-providers.md` § Task 2.0
- Requirements: `docs/requirements/migration-squashing-providers.md` R-P4
- ADR-0019 amendment scope: `docs/decisions/0019-migration-squash-replaces-graph.md`
- Postgres-reference canonicalizer (shape this work mirrors): `src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSnapshotCanonicalizer.cs`
- Aerospike canonicalizer (precedent for opaque-content + structural-canonical split): `src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeSnapshotCanonicalizer.cs`
- Phase 0 Appendix C: OpenSearch ephemeral-fields list (`creation_date`, `uuid`, `version`, `policy_version`, `last_updated_time`)
