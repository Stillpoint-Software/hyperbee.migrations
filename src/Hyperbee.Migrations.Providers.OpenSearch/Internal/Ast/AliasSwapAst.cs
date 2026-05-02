#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// ALIAS SWAP <alias> FROM <old> TO <new>
//
// Per R-16 + NF-2: compiles to a single atomic POST /_aliases body containing
// both remove and add actions. The precondition (alias currently points at
// `old`) is expressed IN THE BODY — the remove action targets `old`, so the
// cluster atomically rejects the whole multi-action body if the precondition
// fails. No separate GET-then-POST window (no TOCTOU).
//
// Example:
//   ALIAS SWAP `users-current` FROM `users-v1` TO `users-v2`
//   -> POST /_aliases { actions: [
//        { remove: { index: "users-v1", alias: "users-current" } },
//        { add:    { index: "users-v2", alias: "users-current" } }
//      ] }

public sealed record AliasSwapAst(
    string Alias,
    string OldIndex,
    string NewIndex
) : StatementAst
{
    public override string Verb => "ALIAS SWAP";
}
