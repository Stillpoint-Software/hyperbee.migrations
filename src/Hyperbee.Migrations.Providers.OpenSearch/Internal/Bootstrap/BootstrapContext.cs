#nullable enable
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;

// Shared context passed to each IBootstrapStep. Steps read from it; they do
// not mutate the context itself (state across steps belongs in step-private
// fields or future BootstrapResult.Steps if cross-step coordination becomes
// necessary).

public sealed class BootstrapContext
{
    public required IOpenSearchClient Client { get; init; }
    public required OpenSearchMigrationOptions Options { get; init; }
    public required TimeProvider TimeProvider { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}
