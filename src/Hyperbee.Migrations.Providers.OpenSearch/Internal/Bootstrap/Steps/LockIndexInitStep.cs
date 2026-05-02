#nullable enable
using Microsoft.Extensions.Logging;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;

// Initializes the migration lock index per R-04 + ADR-0013.
//
// CRITICAL: lock index ships with `number_of_replicas: 0` to eliminate
// replica-write coupling on the lock primary shard (PA-2 mitigation from
// assessment 0002). N concurrent runners attempting to acquire the lock
// would otherwise serialize through replica acks, multiplying tail latency
// for losers.
//
// Per ADR-0013: same AssumeIndicesExist semantics as LedgerIndexInitStep —
// idempotent create by default; verify-only when the deploy role lacks
// indices:admin/create.

public sealed class LockIndexInitStep : IBootstrapStep
{
    public string Name => "lock-init";

    private static readonly string DefaultMappingJson = """
        {
          "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
          "mappings": {
            "dynamic": "strict",
            "properties": {
              "name":          { "type": "keyword" },
              "owner":         { "type": "keyword" },
              "acquiredAt":    { "type": "date" },
              "lastHeartbeat": { "type": "date" }
            }
          }
        }
        """;

    public async Task<StepOutcome> ExecuteAsync( BootstrapContext context )
    {
        var start = context.TimeProvider.GetTimestamp();
        var logger = context.LoggerFactory.CreateLogger<LockIndexInitStep>();
        var indexName = context.Options.LockIndex;

        try
        {
            var existsResponse = await context.Client.Indices.ExistsAsync(
                indexName, ct: context.CancellationToken
            ).ConfigureAwait( false );

            if ( existsResponse.Exists )
            {
                logger.LogDebug( "{step} lock index `{idx}` already exists", Name, indexName );
                var elapsed = context.TimeProvider.GetElapsedTime( start );
                return StepOutcome.Succeeded( Name, elapsed, "exists" );
            }

            if ( context.Options.AssumeIndicesExist )
            {
                var elapsed = context.TimeProvider.GetElapsedTime( start );
                var ex = new OpenSearchProviderException(
                    $"{Name} requires the lock index `{indexName}` to exist " +
                    $"because AssumeIndicesExist=true. Create it manually with " +
                    $"number_of_replicas=0 (PA-2 mitigation) and the required " +
                    $"keyword/date mapping fields before starting the runner." );
                return StepOutcome.Failed( Name, elapsed, ex, "missing lock under AssumeIndicesExist" );
            }

            logger.LogInformation( "{step} creating lock index `{idx}` (replicas=0)", Name, indexName );

            var createResponse = await context.Client.LowLevel.Indices.CreateAsync<StringResponse>(
                indexName,
                PostData.String( DefaultMappingJson ),
                ctx: context.CancellationToken
            ).ConfigureAwait( false );

            if ( !createResponse.Success )
            {
                var detail = createResponse.OriginalException?.Message ?? createResponse.Body ?? "Unknown create failure";
                var ex = new OpenSearchProviderException(
                    $"{Name} could not create lock index `{indexName}`. {detail}",
                    createResponse.OriginalException ?? new InvalidOperationException( detail ) );
                var failedElapsed = context.TimeProvider.GetElapsedTime( start );
                return StepOutcome.Failed( Name, failedElapsed, ex, detail );
            }

            var totalElapsed = context.TimeProvider.GetElapsedTime( start );
            return StepOutcome.Succeeded( Name, totalElapsed, $"created `{indexName}`" );
        }
        catch ( OperationCanceledException )
        {
            throw;
        }
        catch ( Exception ex )
        {
            var elapsed = context.TimeProvider.GetElapsedTime( start );
            return StepOutcome.Failed( Name, elapsed, new OpenSearchProviderException(
                $"{Name} threw an unexpected exception. {ex.Message}", ex ) );
        }
    }
}
