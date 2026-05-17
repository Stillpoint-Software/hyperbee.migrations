---
layout: default
title: OpenSearch FAQ - Template Propagation
parent: OpenSearch Provider
nav_order: 1
---

# Template Propagation FAQ - OpenSearch

The single most common production question on OpenSearch migrations is some form of:

> I changed my mapping (or template, or settings, or analyzer). Why isn't existing data seeing the change?

This page is the canonical answer.

## Why this surprises people

If you're coming from a relational database, you probably expect "alter the schema, the data conforms." OpenSearch doesn't work that way. Each document is indexed against the mapping that existed at the time of write. Changing the mapping changes how new documents get indexed; it does NOT reindex existing ones.

The same applies to:

- **Index templates and component templates.** Templates apply at index-creation time. Existing indices that matched a previous template aren't retroactively rewritten when you update the template.
- **Static index settings.** number_of_shards, codec, analysis chain - any setting marked "static" is fixed at creation. UPDATE SETTINGS without CLOSE rejects them; UPDATE SETTINGS with CLOSE applies them only to the index in question, not to historic data.
- **Analyzers.** Changing an analyzer changes how new tokens get produced for new documents. Existing documents still carry the tokens they were indexed with.

The provider's UPDATE MAPPING dispatcher emits a diagnostic INFO log on every successful mapping update naming this gotcha and pointing at the answer (below). If you're seeing that log, the diagnostic is working as intended.

## The answer: MIGRATE INDEX

```
MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template VIA ALIAS users-current
```

That one line is the canonical mapping-propagation pattern. It expands at parse time into:

1. `CREATE INDEX users-v2` with the body fetched from the live `users-template`.
2. `REINDEX FROM users-v1 TO users-v2` with `op_type: create` auto-injected (so retries don't double-write).
3. `ALIAS SWAP users-current FROM users-v1 TO users-v2` atomically.

Application reads come through the alias `users-current`. After the swap, the alias points at v2. Zero downtime; no writes lost; mapping changes are now applied to the data.

## Step-by-step walkthrough

### Before

You have an index `users-v1` with the old mapping, and your application reads from the alias `users-current`:

```
users-v1  <-- users-current  (alias)
```

### Author the new shape

Update the template to reflect the new mapping:

```
CREATE TEMPLATE users-template WITH BODY @users-template-v2.json
```

The template file holds the new mapping. New indices matching the template's `index_patterns` will pick it up.

But existing data is still on v1 with the old shape. UPDATE MAPPING ON users-v1 won't retroactively rewrite anything.

### Run the migration

```
MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template VIA ALIAS users-current
```

What happens at dispatch time:

1. **CREATE INDEX users-v2** - the provider fetches the live template body and uses it as the new index shape.
2. **REINDEX FROM users-v1 TO users-v2** - the cluster bulk-copies documents from v1 to v2. New mapping applies; documents that don't fit the new mapping fail explicitly (rather than silently mis-typing).
3. **ALIAS SWAP users-current FROM users-v1 TO users-v2** - one atomic _aliases body containing both the remove-from-v1 and add-to-v2 actions. The cluster atomically rejects the whole body if v1 is no longer where the alias points (no TOCTOU window).

### After

```
users-v1                                 (still exists, no alias)
users-v2  <-- users-current  (alias)
```

Application reads through the alias now hit v2. v1 is still around for safety; you can drop it in a follow-up migration once you're confident.

## Common variations

### Inline body instead of a template

If you don't want to manage the new shape via an index template:

```
MIGRATE INDEX users-v1 TO users-v2 WITH BODY $newShape VIA ALIAS users-current
```

with the new mapping in the `bodies` section of the same statement.

### Without the alias swap

```
MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template
```

Creates v2 and reindexes, but leaves the alias alone. Use this if your application doesn't read through an alias, or if you intend to retain both indices for read-traffic comparison before cutover.

### When the source has active writes during the migration

The standard reindex captures only documents present at the time it starts. Writes against v1 during the reindex do NOT make it into v2 automatically. For write-during-migration scenarios, two patterns:

- **Dual write**: application writes to both v1 and v2 during the migration window, then reads switch over.
- **Post-swap delta reindex**: rerun the reindex from a saved checkpoint after the swap to catch v1 writes that arrived during the window.

The composite verb explicitly does NOT solve the dual-write problem - that's an application concern, not a migration tool concern.

## Why not just UPDATE MAPPING?

You can use UPDATE MAPPING to add fields to an existing index. New documents will have the new fields available; queries that filter on the new field will work for those new documents.

You CANNOT use UPDATE MAPPING to:

- Change the type of an existing field (string -> integer, keyword -> text, etc.)
- Remove a field
- Change an analyzer's output for existing documents
- Apply a new dynamic-mapping policy to historic data

Those changes require a reindex. MIGRATE INDEX is the canonical way to do that reindex safely.

## Why not just reindex by hand?

You can. The OpenSearch provider's REINDEX verb is a first-class statement; you can write CREATE + REINDEX + ALIAS SWAP as three separate statements. Sample 2 (`AliasSwapReindexHandComposed`) shows that long-form pattern.

The reasons MIGRATE INDEX is the recommended pattern:

- **Safe defaults are baked in.** `op_type: create` is auto-injected on REINDEX so retried runs don't double-write. The ALIAS SWAP precondition is in-body so there's no TOCTOU window.
- **Atomicity is explicit.** The sub-statements run as a halting sequence; failure of any sub-statement halts the rest and feeds R-19 partial-rollback ledger semantics.
- **The intent is readable.** "MIGRATE INDEX users-v1 TO users-v2" reads as the operation it is. Three separate statements bury the intent across multiple lines.
- **Template resolution is offline-pure.** The parser carries the template name unresolved; the runtime fetches the live template body just before CREATE INDEX dispatch. Authors can update the template independently of the migration that uses it.

## Related

- [OpenSearch Provider](opensearch.md) - main provider page
- [Resource Migrations](resource-migrations.md) - file-based migration patterns
- [Concepts](concepts.md) - cross-cutting concepts (profiles, contexts, journaling, locking)
- Sample 6 in `runners/samples/Hyperbee.Migrations.OpenSearch.Samples` - working demonstration of the full pattern
