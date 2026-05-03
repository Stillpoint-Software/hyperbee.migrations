#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// Composite statement: a single source-line verb whose semantics decompose into
// a deterministic ordered sequence of underlying foundation-verb statements.
// MIGRATE INDEX (R-30) is the canonical example; its decomposition is performed
// at parse time, producing the same AST shape as the hand-composed equivalent.
//
// The dispatcher recognizes CompositeStatementAst and walks Children sequentially,
// halting on the first failure. Each child runs through normal middleware
// (implicit waits, scrubbing, observability). The composite's own Verb is what
// surfaces in the migration ledger; child verbs are nested in StatementResult
// detail messages.

public sealed record CompositeStatementAst(
    string CompositeVerb,
    StatementAst[] Children
) : StatementAst
{
    public override string Verb => CompositeVerb;
}
