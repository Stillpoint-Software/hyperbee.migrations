#nullable enable
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

// WaitForStatus lives in OpenSearch.Net; alias it directly so the inline
// switch arms below stay readable.
using WaitForStatus = OpenSearch.Net.WaitForStatus;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;

// Second step in the default pipeline (per ADR-0014). Polls cluster health
// until the configured threshold (Yellow or Green per R-03) is reached.
//
// Single-node clusters with replicas > 0 cannot reach Green — that's by
// design (no node to host replica shards). The SDK default is Yellow; the
// production-defaults extension method (ADR-0012) flips to Green for
// real multi-node deployments.
//
// The step uses OpenSearch's blocking `wait_for_status` query parameter
// rather than client-side polling. This keeps the master's task queue in
// charge of timing and avoids client-side polling storms (mitigates
// PA-12 from assessment 0002).

public sealed class ClusterHealthStep : IBootstrapStep
{
    public string Name => "cluster-health";

    public async Task<StepOutcome> ExecuteAsync( BootstrapContext context )
    {
        var start = context.TimeProvider.GetTimestamp();
        var logger = context.LoggerFactory.CreateLogger<ClusterHealthStep>();

        var threshold = context.Options.ClusterHealthThreshold switch
        {
            ClusterHealthThreshold.Green => WaitForStatus.Green,
            _ => WaitForStatus.Yellow
        };

        var timeout = context.Options.ImplicitWaitTimeout;

        try
        {
            logger.LogDebug( "{step} waiting for {threshold} (timeout {timeout})", Name, threshold, timeout );

            var response = await context.Client.Cluster.HealthAsync(
                selector: r => r
                    .WaitForStatus( threshold )
                    .Timeout( timeout ),
                ct: context.CancellationToken
            ).ConfigureAwait( false );

            var elapsed = context.TimeProvider.GetElapsedTime( start );

            if ( !response.IsValid )
            {
                var detail = response.OriginalException?.Message ?? "Cluster health request did not return a valid response.";
                var ex = new OpenSearchNotReadyException(
                    $"{Name} could not retrieve cluster health. {detail}",
                    response.OriginalException ?? new InvalidOperationException( detail ) );
                return StepOutcome.Failed( Name, elapsed, ex, detail );
            }

            if ( response.TimedOut )
            {
                var ex = new OpenSearchNotReadyException(
                    $"{Name} timed out waiting for cluster status {threshold}. " +
                    $"Current status: {response.Status}, active shards percent: {response.ActiveShardsPercentAsNumber:F1}%." );
                return StepOutcome.Failed( Name, elapsed, ex, $"timed out at {response.Status}" );
            }

            logger.LogInformation(
                "{step} reached status {status} in {duration} (active shards {active}%)",
                Name, response.Status, elapsed, response.ActiveShardsPercentAsNumber );

            return StepOutcome.Succeeded( Name, elapsed, $"status={response.Status}" );
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
