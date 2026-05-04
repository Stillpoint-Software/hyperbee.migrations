# ADR-0016: OpenSearch Provider Does Not Use File-Level Templating

**Status:** Accepted
**Date:** 2026-05-02

## Context

During Phase 0 of the OpenSearch provider implementation, requirement R-10 introduced Hyperbee.Templating as a four-scope renderer that would run before the Parlot parser. The justification was that OpenSearch resource bodies (settings + mappings + properties + analyzers + ISM policies) are larger and have env-variant pieces embedded inside JSON, not at the call site — so file-level substitution / conditionals / iteration looked attractive.

After Task 0.4 landed (the Templating first-contact spike) the maintainer raised a sharper question: *no other provider uses Hyperbee.Templating; why does this one?*

Audit of the existing four providers confirms the divergence:

| Provider | Env-variant handling |
|---|---|
| Aerospike | Typed options: `Namespace`, `MigrationSet`, `LockName` resolved at runtime by the resource runner |
| Couchbase | Typed options: bucket/scope/collection identifiers; component template bodies vary by code, not by templated text |
| MongoDB | Typed options: `DatabaseName`, `CollectionName` |
| Postgres | Typed options: `Schema`; raw `.sql` files use Postgres-side parameter binding |

None ship a templating engine. Env-variation is handled by typed `MigrationOptions` properties + per-environment `appsettings.{Environment}.json`.

The forces in tension during the original decision:

- **House-style consistency** vs **OpenSearch's larger body sizes**
- **Speculative needs** (conditional sections, iteration) vs **demonstrated needs** (string substitution)
- **In-house engine reuse** vs **first-contact bug class** (PM-5 from assessment 0002 specifically warned about this — the spike did surface 4 real first-contact issues in Hyperbee.Templating 3.4.1)

The Phase 0 spike (Task 0.4) DID validate that the engine works. But validation that *something is feasible* is not the same as *justification that it should be adopted*.

Re-examination shows: the only concrete need is **string substitution** (env-variant index names, replica counts, analyzer paths). Conditional sections and iteration are speculative — no current sample, no R-30 example, and no production scenario test requires them. String substitution is exactly what typed options + runtime substitution already provide in the other four providers.

## Decision

The OpenSearch provider does NOT use Hyperbee.Templating or any other file-level templating engine. It matches the house pattern of the other four providers:

- **Env-variant values** are typed properties on `OpenSearchMigrationOptions` (e.g., `IndexPrefix`, future `ReplicaCount`)
- **Resource files** use bracketed identifiers or sibling JSON properties that the runtime substitutes by name (the same `WITH BODY $name` pattern from R-09)
- **Per-environment configuration** flows through `appsettings.{Environment}.json` and `IConfiguration` binding, identical to the runner pattern of the other providers

Specifically, this ADR strikes/amends:

- **R-10 (Hyperbee.Templating renderer)** — struck entirely
- **R-25 SecretScrubber routing** — amended to plain structured logging; secret redaction (if needed) is a future Serilog-config concern, not a provider design concern
- **Phase 0 Task 0.4** — work product (Templating spike code) deleted; the validation that the engine works is preserved as a learning, not as code
- **Phase 6 Tasks 6.1, 6.2** — removed from the plan
- **R-30 `MIGRATE INDEX` `WITH TEMPLATE`** — runtime template-body resolution still happens (per ADR-0015) but no Hyperbee.Templating involvement; the template body is a JSON document fetched from the cluster, not a rendered text artifact

## Consequences

**Easier:**
- House style consistency — operators reading code across all five providers see the same env-variation pattern
- Zero first-contact bug risk class from Hyperbee.Templating; eliminates the four documented PM-5 quirks (`{{if}}` vs `{{#if}}`, dotted-key validator override, fat-arrow rewriter limitation, missing `each n,i` index variant)
- Smaller dependency graph — `Hyperbee.Templating` removed from `Directory.Packages.props`
- Smaller surface area for review and maintenance

**Harder:**
- Authors who genuinely need conditional sections or iteration in resource files must either (a) write them in code via the migration class's `UpAsync`, (b) split into multiple migrations, or (c) generate the resource file at build time with their own templating tool
- The `WHEN VERSION`/`context` runtime conditional execution (R-15) remains the only conditional mechanism; it operates on whole statements, not on JSON-body fragments
- If a future need for conditional bodies emerges, that's a new ADR + new design — not a quiet feature add

**Constrains:**
- Re-introducing Hyperbee.Templating (or any templating engine) requires a superseding ADR with a documented use case that typed options cannot satisfy
- Future verbs that need env-variant pieces inside their JSON bodies must follow the typed-options + runtime-substitution pattern, not introduce templating ad hoc
- The `SecretMarker`/`SecretScrubber` design surface is removed from the provider; option-value redaction in logs (if desired) belongs at the host Serilog/ILogger configuration level, applying uniformly across all providers
