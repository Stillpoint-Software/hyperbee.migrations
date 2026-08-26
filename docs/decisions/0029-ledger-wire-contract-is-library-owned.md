# ADR-0029: The Ledger's Wire Contract is Library-Owned

**Status:** Accepted
**Date:** 2026-08-25
**Related ADRs:** ADR-0003 (Provider Record Store Contract), ADR-0018 (Split Ledger and Lock Indices), ADR-0019 (Replaces-Graph Mechanism), ADR-0021 (Migration Record Checksum)

## Context

Every provider record store persists `MigrationRecord` — a type the library owns, keeps out of the consumer's domain model, and reads back with queries the library also owns. But the *client* that carries those documents to the database is, in four of five providers, registered by the consumer (`IAsyncClient`, `IClusterProvider`, `IMongoClient`, `NpgsqlDataSource`), and in the fifth (OpenSearch) is constructible either by the library or the consumer.

Modern database clients infer a great deal from consumer-level configuration: which index a CLR type maps to, what casing a property serializes as, which serializer runs. When a record store leans on that inference, it has silently made the ledger's wire contract depend on configuration the library does not control and cannot see.

Two shipped defects made the cost concrete.

**OpenSearch (v3.0.0–v3.1.0, breaking every run).** ADR-0019 Phase 3 added `IntersectWithAppliedAsync`, implemented as a single `_mget`. An `_mget` carries an index in the URL *and* in each body entry. The implementation set the URL one explicitly from `OpenSearchMigrationOptions.LedgerIndex` and let the body entries default — which resolves via `IndexName.From<OpenSearchMigrationRecord>()`, reading `ConnectionSettings.DefaultMappingFor<T>()` / `DefaultIndex()`. Neither `AddOpenSearchClient` nor `AddOpenSearchAwsClient` configures either, so request serialization threw `Index name is null for the given type and no default index is set` before a byte reached the wire. `MigrationRunner.RunAsync` calls `IntersectWithAppliedAsync` unconditionally whenever at least one migration is discovered, so every OpenSearch run died — including through the library's own runner and CLI.

**MongoDB (silent, since v3.0.0).** `IntersectWithSquashedAsync` mixes two ways of naming the same document's fields: a typed expression for `Kind` (routed through the BSON class map, rendering `Kind`) and a raw string literal for `replaces` (rendered verbatim). The driver's default element name is the member name, so the writer stores `Replaces`. The rendered filter — `{ "Kind": 1, "replaces": { "$in": [...] } }` — can never match. Squash reconciliation returned empty forever, and squashed migrations re-ran.

The two look unrelated. They are the same mistake: the ledger's identity on the wire was allowed to depend on something other than the library's own configuration.

## Decision

The ledger's wire contract — which index/collection/table a ledger document lives in, and what its fields are named on the wire — is **library-owned**. Two rules follow.

### Rule 1 — Never resolve library-private document identity through consumer-owned client configuration

Ledger requests must carry their target explicitly, sourced from `{Provider}MigrationOptions`. Type-driven inference (`DefaultMappingFor<T>`, `DefaultIndex`, ambient collection conventions) is off-limits for ledger operations, at every level of the request — URL *and* body.

```csharp
// OpenSearch: per-operation index, not just the URL index
.GetMany<OpenSearchMigrationRecord>( ids, ( op, _ ) => op.Index( _options.LedgerIndex ) )
```

The library must never require a consumer to declare a mapping for a type the library keeps internal. `OpenSearchMigrationRecord` is not part of the consumer's domain; demanding a `DefaultMappingFor<OpenSearchMigrationRecord>` in the consumer's client wiring inverts ownership.

### Rule 2 — Route every reference to a ledger field through the same path as the writer

Where a query names the library's own document fields, every reference goes through the serialization path the writer uses — typed expressions, never a mix of typed expressions and string literals.

```csharp
// MongoDB: both terms typed, so both render through the class map
Builders<MigrationRecord>.Filter.And(
    Builders<MigrationRecord>.Filter.Eq( x => x.Kind, MigrationRecordKind.Squash ),
    Builders<MigrationRecord>.Filter.AnyIn( x => x.Replaces, inputs ) );
```

This is deliberately *not* "pin the element names with attributes or a class map." Pinning would repair the library's self-consistency at the cost of orphaning any deployment whose consumer already registered a naming convention — their existing ledger is self-consistent under that convention today, and a pinned map would stop reading it. Routing everything through one path is self-consistent under *any* configuration, including none, and changes no bytes for anyone.

Where a provider's query language cannot express typed field references (N1QL, SQL), the library pins the serialization instead — see the Couchbase note below.

### Rule 3 — Test the wire, not the mock

Provider record stores get a test tier between "substitute the client" and "start a container": the real client and real serializer over a faked transport (`InMemoryConnection` for OpenSearch, rendered filter/serializer output for MongoDB and Couchbase). Both defects above are invisible to a substituted client and were invisible to a suite that had only mock-tier and container-tier tests.

Every provider ships a wire test asserting that (a) ledger operations succeed on a client with no library-specific configuration, and (b) the names its queries use match the names its writer produces.

## Consequences

**Positive:**
- The ledger works on any correctly-authenticated client, however the consumer built it. Bring-your-own-client is a supported path rather than an accident.
- The failure class becomes testable in milliseconds without Docker, so it is caught by the CI tier that actually runs.
- Consumers keep full freedom over client configuration for *their* types; the library stops competing for that surface.

**Negative:**
- Ledger call sites are more verbose. The per-operation index on `_mget` reads as redundant next to the URL index and will look like a candidate for "simplification" to a future reader — hence the load-bearing comment at the call site pointing here.
- Rule 2 forbids the tidier-looking string literal even where it is currently correct.

**Neutral:**
- Postgres and Aerospike already satisfy this by construction (explicit SQL columns, explicit bin names). The ADR names an invariant they already meet rather than asking anything new of them.
- Couchbase satisfies Rule 1 (keyspace is explicit) but cannot satisfy Rule 2 through typed expressions — N1QL references `m.kind` / `m.replaces` as text. It pins the ledger serializer instead so those names are guaranteed rather than inherited from `ClusterOptions.Serializer`.

## Alternatives Considered

- **Configure `DefaultMappingFor<OpenSearchMigrationRecord>` inside `AddOpenSearchClient`** — rejected. The ledger index is runtime configuration on `OpenSearchMigrationOptions`, which the client factory cannot see: `AddOpenSearchClient` runs independently of, and often before, `AddOpenSearchMigrations`. It would create a registration-order dependency and still fail for anyone registering their own `IOpenSearchClient`.
- **Expose `ConnectionSettings` so consumers can declare the mapping themselves** — rejected *as the fix*. It makes correct operation opt-in and pushes a library-internal detail into consumer wiring. The escape hatch is worth having on its own merits (see ADR-0030) but it is not the remedy for this defect.
- **Pin ledger element names with serializer attributes or a registered class map** — rejected for MongoDB; see Rule 2. Adopted for Couchbase only because N1QL leaves no typed alternative.
- **Make queries tolerant of multiple casings** (`WHERE m.kind = 1 OR m.Kind = 1`) — rejected. Covers two conventions out of unboundedly many and entrenches the ambiguity instead of removing it.

## References

- [`OpenSearchRecordStore.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchRecordStore.cs) — `IntersectWithAppliedAsync`
- [`MongoDBRecordStore.cs`](../../src/Hyperbee.Migrations.Providers.MongoDB/MongoDBRecordStore.cs) — `IntersectWithSquashedAsync`
- [`CouchbaseRecordStore.cs`](../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseRecordStore.cs) — `IntersectWithSquashedAsync`
- `OpenSearchLedgerWireTests`, `MongoDBLedgerWireTests`, `CouchbaseLedgerWireTests`
