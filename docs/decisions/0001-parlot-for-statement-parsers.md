# ADR-0001: Use Parlot for Statement Parsers

**Status:** Accepted
**Date:** 2026-04-03
**Deciders:** Brenton Farmer

## Context

The Couchbase provider uses hand-written regex-based parsers (`StatementParser.cs`) and a state-machine scanner (`KeyspaceParser.cs`) for parsing pseudo-N1QL statements from JSON resource files. As we add MongoDB statement parsing and a new Aerospike provider (with AQL corner cases like async index creation and directives), we need a scalable parsing strategy.

Options considered:
1. **Continue with regex** — fragile, hard to extend, poor error messages
2. **ANTLR** — powerful but requires separate grammar files and code generation step
3. **Parlot** — fast .NET parser combinator, fluent API, no build tooling required
4. **Sprache/Pidgin** — similar to Parlot but slower and less actively maintained

## Decision

Adopt **Parlot** for all new statement parsers (MongoDB, Aerospike). Do not rewrite the existing Couchbase parser — it works and is scoped to a known set of statements.

## Rationale

- Parlot is already used in the organization's `hyperbee.xs` project — team familiarity exists
- No build-time code generation (unlike ANTLR) — grammars are pure C#
- 10x faster than Pidgin, 12-24x faster than Sprache (irrelevant for migration scripts, but indicates quality)
- Built-in `Terms.Keyword()` with case-insensitive, boundary-aware matching is ideal for SQL-like keywords
- `ElseError()` provides actionable parse failure messages for migration authors
- Composable: common grammar elements (identifiers, namespace refs) can be shared across providers

## Consequences

- New dependency: `Parlot` NuGet package added to MongoDB and Aerospike provider projects
- Couchbase parser remains regex-based (can be migrated later if desired)
- Parser tests should validate both happy-path and error-message quality
