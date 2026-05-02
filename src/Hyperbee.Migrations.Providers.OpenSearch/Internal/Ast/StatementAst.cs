#nullable enable
using System.Text.Json.Nodes;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// Statement AST root. Per ADR-0011 + ADR-0015, the parser produces these nodes
// offline (no I/O). Each derived record carries the verb-specific payload AND
// any safe-default flags resolved at parse time. Runtime middleware consumes the
// flags during request build.

public abstract record StatementAst
{
    public abstract string Verb { get; }
}

// Body source — the discriminated union of ways a `WITH BODY <ref>` clause
// resolves to a JSON body at dispatch time. Per ADR-0017 there are two grammar
// forms, mapped to the two variants below.

public abstract record BodySource;

// `WITH BODY $name` — resolves to `bodies.<Name>` first, falling back to a
// top-level sibling property `<Name>` for back-compat with ADR-0009.
// The named value is itself either an inline JSON object OR a `@path` file
// reference string (resolved by the resource runner).

public sealed record BodyRef( string Name ) : BodySource;

// `WITH BODY @path/to/file.json` — resolves the path against the migration's
// own resource folder. The file must be marked `EmbeddedResource` in the csproj
// (same convention as `statements.json`). Path is rejected at parse time if it
// is absolute, contains `..`, or contains other suspect characters.

public sealed record BodyFileRef( string Path ) : BodySource;

// Reference to an OpenSearch index template whose `template` block becomes the
// body for a CREATE INDEX. Carried unresolved through parsing (ADR-0015 — parser
// is offline-pure); resolved at dispatch time via runtime middleware that
// performs `GET /_index_template/<TemplateName>` immediately before CREATE
// INDEX is dispatched. Used by the MIGRATE INDEX composite verb (R-30).

public sealed record TemplateBodyRef( string TemplateName );
