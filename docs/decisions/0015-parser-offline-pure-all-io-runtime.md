# ADR-0015: Parser is Offline-Pure; All I/O is Runtime Middleware

**Status:** Accepted
**Date:** 2026-05-02

## Context

ADR-0011 established a hybrid parser+runtime injection architecture: parser owns intent (AST flags, parse-time syntactic validation, justification token validation, semver comparison); runtime middleware owns execution (JSON tree merge, scoped implicit waits, Tasks API polling, secret scrubbing).

During plan assessment 0003, the Independent Review identified an architectural commitment buried in R-30's `MIGRATE INDEX ... WITH TEMPLATE <id>` semantics that ADR-0011 did not explicitly address: the original R-30 wording suggested the parser would perform `GET /_index_template/<id>` *at parse time* to resolve the template body. This contradicts ADR-0011's intent in three ways:

1. **Offline parse becomes impossible.** Parser unit tests cannot run without a live OpenSearch cluster (or extensive mocking) — parser tests should be fast and not require Docker.
2. **Error semantics are confused.** "Template not found at parse time" surfaces as a grammar/parse error to consumers; "template not found at execute time" surfaces as an operational error. The two should not be conflated.
3. **The parser/runtime boundary becomes ambiguous.** ADR-0011 said "parser owns intent; runtime owns execution," but did not state explicitly that the parser performs no I/O. Implementers reading R-30 in isolation could reasonably build either architecture.

The forces in tension: implementer convenience (parser doing template lookup gives early feedback) vs. architectural invariants (parser purity, test speed, predictable error semantics, clear concern boundaries).

## Decision

The Parlot grammar and AST construction layer is **offline-pure**: it performs no network I/O, no file I/O, and no live cluster lookups. All I/O — including `GET /_index_template/<id>` lookups for `MIGRATE INDEX ... WITH TEMPLATE` — happens in runtime middleware immediately before the dispatched request executes.

Specifically:

- **Parser produces unresolved-reference AST nodes** for any value that requires live cluster state. `MIGRATE INDEX ... WITH TEMPLATE foo` produces an AST whose `CreateIndex` sub-node carries `BodySource = TemplateRef("foo")` rather than a resolved body.
- **Runtime resolution middleware** materializes those unresolved references during request build, immediately before HTTP dispatch. Errors at this stage surface as `OpenSearchTemplateResolutionException` (or similar typed exception), not as parse errors.
- **Parse-time errors** are restricted to grammar (malformed verb), syntactic (forbidden patterns per R-18), name-policy (reserved scope/identifier collisions per R-09), and value-shape (semver per R-15a).

This is a clarifying corollary of ADR-0011, not a supersedure: ADR-0011's hybrid decision stands. ADR-0015 makes the parser/runtime boundary explicit so future verb additions don't drift across it.

## Consequences

**Easier:**
- Parser unit tests run without Docker — fast feedback loop on grammar work
- Parse errors and runtime errors have distinct, untangled error types
- New verbs that need runtime context (e.g., `WHEN INDEX EXISTS`) follow a clear pattern: emit unresolved-reference AST, resolve at runtime
- The "where does I/O happen?" question has one answer for every verb

**Harder:**
- Author who writes `MIGRATE INDEX ... WITH TEMPLATE foo` doesn't get parse-time feedback that `foo` doesn't exist — discovery is delayed to execution. Mitigated: error message at execute time names the template explicitly and links to documented alternatives
- Implementers must resist the urge to "validate during parse for better UX" — every such case becomes a justification-required ADR amendment, not a casual decision
- Some structural validations (e.g., "CREATE INDEX statement's $body actually exists") happen at parse, but reference resolution does not — implementers must distinguish "this name is a syntactic identifier" from "this name resolves to live state"

**Constrains:**
- All future verbs that need cluster state must use unresolved-reference AST + runtime middleware. No exceptions without a superseding ADR
- The Parlot grammar definitions must not import OpenSearch.Client types for I/O (they may import value types like `IndexName` for parsing)
- Runtime middleware exception types are part of the public contract — naming and behavior are stable
