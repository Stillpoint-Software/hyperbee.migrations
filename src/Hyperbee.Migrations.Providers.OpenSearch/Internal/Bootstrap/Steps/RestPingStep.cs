#nullable enable
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;

// First step in the default pipeline (per ADR-0014). Calls OpenSearch's root
// endpoint and verifies a successful response; this is the cheapest possible
// "is the cluster reachable?" probe. Distinguishes a network/auth failure
// (cluster unreachable) from a cluster-state failure (reachable but red),
// which is what ClusterHealthStep checks next.

public sealed class RestPingStep : IBootstrapStep
{
    public string Name => "rest-ping";

    public async Task<StepOutcome> ExecuteAsync( BootstrapContext context )
    {
        var start = context.TimeProvider.GetTimestamp();
        var logger = context.LoggerFactory.CreateLogger<RestPingStep>();

        try
        {
            logger.LogDebug( "{step} pinging {endpoint}", Name, context.Client.ConnectionSettings.ConnectionPool.Nodes );

            var response = await context.Client.PingAsync( ct: context.CancellationToken ).ConfigureAwait( false );

            var elapsed = context.TimeProvider.GetElapsedTime( start );

            if ( !response.IsValid )
            {
                var detail = response.OriginalException?.Message
                    ?? response.ServerError?.Error?.ToString()
                    ?? "Unknown ping failure";

                var ex = new OpenSearchNotReadyException(
                    $"{Name} could not reach the cluster. {detail}",
                    response.OriginalException ?? new InvalidOperationException( detail ) );

                return StepOutcome.Failed( Name, elapsed, ex, detail );
            }

            logger.LogInformation( "{step} succeeded in {duration}", Name, elapsed );
            return StepOutcome.Succeeded( Name, elapsed );
        }
        catch ( OperationCanceledException )
        {
            throw;
        }
        catch ( Exception ex )
        {
            var elapsed = context.TimeProvider.GetElapsedTime( start );
            return StepOutcome.Failed( Name, elapsed, new OpenSearchNotReadyException(
                $"{Name} threw an unexpected exception. {ex.Message}", ex ) );
        }
    }
}
