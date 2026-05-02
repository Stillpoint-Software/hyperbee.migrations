# ADR-0011: Hybrid Parser+Runtime Injection for OpenSearch Safe Defaults

**Status:** Accepted
**Date:** 2026-05-02

## Context

The OpenSearch provider must apply safe defaults to prevent silent data corruption. Two are load-bearing:

- `op_type: create` injection on `REINDEX` request bodies (closes PM-3 from assessment 0002 — re-runs of a partially-completed reindex would otherwise double-write or skip new docs)
- `dynamic: strict` injection on `CREATE INDEX` mappings (eliminates mapping explosion; per R-17 must be component-template-aware: skipped when body has `composed_of`)

Two extreme architectures were rejected:

1. **Pure runtime middleware** (Approach A in `/nop:propose` for this provider) — applied during request dispatch on fully-built JSON. Cannot satisfy R-18's parse-time syntactic detection of unsafe ops with file/line/recognized-verb error context; component-template detection requires a JSON-tree walk on every dispatch; UNSAFE/NO WAIT justification token validation must happen at parse anyway. Existing providers (Couchbase, Aerospike, MongoDB) use pure runtime patterns, but those providers don't face JSON-body-merging hazards at OpenSearch's scale.

2. **Pure parser** (Approach B in propose) — AST emits a final correct payload; runtime is a thin transport. Cannot route logs through `SecretScrubber` (R-10/R-25), cannot emit structured WARN events from response paths, cannot observe Tasks API progress. Loses runtime observability entirely.

The assessment 0002 meta-finding established that *"documentation as a fix for correctness hazards on the laziest path is anti-pattern."* Safe defaults must be enforced in code, not documented in samples. The Independent Review's pattern claim (Red-Blue₂ Phase 3.75) was validated 4-of-5 contested, demanding parser-level enforcement for `op_type: create`, component-template-aware `dynamic: strict`, and `ALIAS SWAP` atomic-precondition.

The forces in tension: parse-time correctness (error messages, structural detection, AST-level intent) vs. runtime concerns (live request/response observation, secret scrubbing, structured event emission). Neither extreme satisfies the requirements.

## Decision

We will use a hybrid: parser owns *intent*, runtime owns *execution*.

**Parser layer (Parlot, per ADR-0001) produces:**
- AST nodes carrying safe-default flags (`op_type:create=true` on `REINDEX`, `dynamic:strict=auto` on `CREATE INDEX`)
- Component-template-aware flag computation (`dynamic:strict=auto` resolves to off when AST body has `composed_of`)
- Parse-time syntactic enumeration of unsafe operations (R-18) with file/index/recognized-verb error context
- UNSAFE/NO WAIT justification token validation (non-empty reason required)
- Semantic version comparison (R-15a) — parsed to `System.Version` at parse time
- `MIGRATE INDEX` composite verb decomposition into `CREATE INDEX` + `REINDEX` + `ALIAS SWAP` AST nodes (R-30)

**Runtime middleware layer applies:**
- `SafeDefaultMergeMiddleware` — merges AST flags into the JSON tree during request build
- `ImplicitWaitMiddleware` — issues scoped `_cluster/health` per `WaitMode` (R-12)
- `TasksApiPollMiddleware` — handles `?wait_for_completion=false` flow (R-11)
- `SecretScrubberSink` — wraps `ILogger`; redacts `SecretMarker` content-hashes from all output (R-10/R-25)

The two layers communicate through the AST. The parser cannot dispatch HTTP; the runtime cannot reject ill-formed grammar.

## Consequences

**Easier:**
- Parse-time errors carry full positional context (file, statement index, recognized-verb-so-far) — operators don't debug runtime stack traces for grammar issues
- Component-template detection is structural (presence of `composed_of` key on the AST) — no fragile JSON-tree walking at runtime
- Safe-default behavior changes are localized: new safe-default → new AST flag + new merge rule; observability changes are middleware-only
- Consumers extending the grammar add AST nodes with flags; they don't write middleware
- Unit tests against the parser are fast and don't require an OpenSearch container

**Harder:**
- Two layers must stay coordinated; the merge logic in middleware must correctly handle arbitrary user-supplied JSON bodies without losing AST flag intent
- The riskiest assumption in this architecture: runtime middleware can correctly merge AST safe-default flags into user-supplied JSON. This must be validated via a Phase 1 spike before any other implementation work
- Documentation must distinguish "parser-resolvable" decisions (compile-time) from "runtime-resolvable" decisions (dispatch-time) — failing to teach this distinction breeds confusion among future maintainers

**Constrains:**
- Any new safe-default behavior must declare its intent at the AST level (parser-resolvable) AND provide a runtime merge path
- Extending grammar via consumer DI is a parser-side decision (Parlot grammar composition); extending observability is a middleware-side decision
- Future ADRs about parser changes must consider whether the change requires a corresponding middleware update
