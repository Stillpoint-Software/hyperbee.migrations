# Hyperbee Migrations OpenSearch Provider

OpenSearch provider for Hyperbee Migrations. Adds support for running migrations against OpenSearch clusters.

## Features

- Migration tracking via dedicated `.migrations` index with strict mapping and forensic fields
- Auto-renewing distributed lock with realtime-GET takeover and bounded lifetime
- Resource migrations: Parlot-parsed statement execution + bulk document seeding
- Hybrid parser+runtime injection for safe defaults (`op_type: create`, `dynamic: strict`)
- Composite `MIGRATE INDEX` verb encoding the canonical zero-downtime reindex-and-swap pattern
- Atomic `ALIAS SWAP` with in-body precondition (no TOCTOU window)
- ISM policy management; composable index templates
- Multi-environment support: single-node dev, multi-node prod, AWS Managed OpenSearch (with SigV4)

## Status

Under active development on `devs/bfarmer/provider-opensearch`. See:

- `docs/requirements/opensearch-provider.md` — 31 testable requirements
- `docs/design/opensearch-provider.md` — Pragmatic Hybrid architecture
- `docs/decisions/0011-0015` — provider-specific ADRs
- `docs/plans/active/opensearch-provider.md` — implementation plan
