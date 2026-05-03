#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]
//
// Polls the OpenSearch Tasks API (R-11) until the named task completes
// (or the optional timeout elapses). Used to wait on long-running
// operations dispatched with `wait_for_completion=false` — most often
// async REINDEX, snapshot, restore, or force-merge.
//
// The TaskId format is OpenSearch's standard `<node>:<task-id>` (e.g.
// "abc123:42"). Authors typically capture this from a prior REINDEX
// statement's response.

public sealed record WaitUntilTaskAst(
    string TaskId,
    TimeSpan? Timeout
) : StatementAst
{
    public override string Verb => "WAIT UNTIL TASK";
}
