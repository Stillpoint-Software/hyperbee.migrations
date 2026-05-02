#nullable enable
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;

// Per-statement execution context passed to StatementDispatcher.DispatchAsync.
//
// ResolvedBody is the sibling JSON property the parser referenced via WITH BODY $name —
// the resource runner (Slice C.2) is responsible for resolving the reference before
// calling DispatchAsync. Null is acceptable for verbs that can dispatch without a body
// (REINDEX, REFRESH, WAIT FOR, WAIT UNTIL TASK, DROP INDEX).

public sealed class StatementContext
{
    public required IOpenSearchClient Client { get; init; }
    public required OpenSearchMigrationOptions Options { get; init; }
    public required TimeProvider TimeProvider { get; init; }
    public required ILogger Logger { get; init; }
    public JsonNode? ResolvedBody { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
