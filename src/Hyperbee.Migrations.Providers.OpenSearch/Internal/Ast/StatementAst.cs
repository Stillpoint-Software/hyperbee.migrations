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

// Reference to a sibling JSON property on the same statement object that holds
// the request body. `WITH BODY $usersIndex` produces BodyRef("usersIndex").
// The body itself is opaque JSON resolved by the calling code, not by the parser.

public sealed record BodyRef( string Name );

// Reference to an OpenSearch index template whose `template` block becomes the
// body for a CREATE INDEX. Carried unresolved through parsing (ADR-0015 — parser
// is offline-pure); resolved at dispatch time via runtime middleware that
// performs `GET /_index_template/<TemplateName>` immediately before CREATE
// INDEX is dispatched. Used by the MIGRATE INDEX composite verb (R-30).

public sealed record TemplateBodyRef( string TemplateName );
