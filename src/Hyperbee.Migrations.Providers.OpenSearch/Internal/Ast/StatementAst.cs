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
