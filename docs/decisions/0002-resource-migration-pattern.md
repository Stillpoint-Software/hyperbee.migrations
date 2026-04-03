# ADR-0002: Standardize Resource Migration Pattern for NoSQL Providers

**Status:** Accepted
**Date:** 2026-04-03
**Deciders:** Brenton Farmer

## Context

The Couchbase provider has a mature resource migration system with two capabilities:
- `StatementsFromAsync` — parses pseudo-N1QL from JSON, executes DDL/DML operations
- `DocumentsFromAsync` — upserts JSON documents into bucket/scope/collection

MongoDB only has `DocumentsFromAsync`. Postgres has `SqlFromAsync` (raw SQL). We want to add statement-based resource migrations to MongoDB and build a new Aerospike provider with similar capabilities.

## Decision

1. **NoSQL providers should support both `StatementsFromAsync` and `DocumentsFromAsync`** via their resource runners.
2. **Standard JSON resource format** across all NoSQL providers:
   ```json
   { "statements": [{ "statement": "..." }] }
   ```
3. **Each provider owns its parser** — statement syntax is provider-specific (N1QL, MongoDB commands, AQL).
4. **SQL providers remain SQL-based** — `SqlFromAsync` with raw `.sql` files is the right pattern for SQL.
5. **Aerospike adds directive comments** (`@RECREATE`, `@WAITREADY`) as metadata in the JSON format to handle async index creation.

## Rationale

- NoSQL databases need DDL-like operations (create collections, indexes) that SQL databases handle natively
- A standard JSON format enables tooling and documentation consistency
- Provider-specific parsers keep each provider self-contained and testable
- Directives solve Aerospike's async index creation without complicating other providers

## Consequences

- MongoDB `MongoDBResourceRunner` gains `StatementsFromAsync` method
- Aerospike provider follows the established pattern from day one
- Each provider's parser is independently testable with unit tests
- The JSON statement format becomes a documented contract
