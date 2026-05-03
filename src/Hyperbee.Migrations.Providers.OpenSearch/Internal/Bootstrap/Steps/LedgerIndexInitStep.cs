#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;

// Initializes the migration ledger index per R-06 + ADR-0013.
//
// Behavior:
//   - AssumeIndicesExist == false (default): idempotent create. If missing,
//     create with the required strict mapping. If present, verify the mapping
//     contains the required forensic fields (id, runOn, direction, status,
//     appliedBy, checksum, error, failedStatementIndex per R-06). Mismatch
//     surfaces OpenSearchLedgerSchemaMismatchException.
//   - AssumeIndicesExist == true: verification only — no create. Used by
//     consumers in tightly-scoped IAM contexts (e.g., AWS Managed where the
//     deploy role lacks indices:admin/create per ADR-0013).
//
// Per ADR-0011: this step uses the low-level client with raw JSON bodies to
// avoid wrestling the high-level POCO mapping API for ledger schema
// verification. The mapping is small and auditable as a JSON literal.

public sealed class LedgerIndexInitStep : IBootstrapStep
{
    public string Name => "ledger-init";

    private static readonly string[] RequiredFields =
    [
        "id", "runOn", "direction", "status", "appliedBy", "checksum", "error", "failedStatementIndex"
    ];

    private static readonly string DefaultMappingJson = """
        {
          "settings": { "number_of_shards": 1, "number_of_replicas": 0 },
          "mappings": {
            "dynamic": "strict",
            "properties": {
              "id":                   { "type": "keyword" },
              "runOn":                { "type": "date" },
              "direction":            { "type": "keyword" },
              "status":               { "type": "keyword" },
              "appliedBy":            { "type": "keyword" },
              "checksum":             { "type": "keyword" },
              "error":                { "type": "text" },
              "failedStatementIndex": { "type": "integer" }
            }
          }
        }
        """;

    public async Task<StepOutcome> ExecuteAsync( BootstrapContext context )
    {
        var start = context.TimeProvider.GetTimestamp();
        var logger = context.LoggerFactory.CreateLogger<LedgerIndexInitStep>();
        var indexName = context.Options.LedgerIndex;

        try
        {
            var existsResponse = await context.Client.Indices.ExistsAsync(
                indexName, ct: context.CancellationToken
            ).ConfigureAwait( false );

            if ( existsResponse.Exists )
            {
                logger.LogDebug( "{step} ledger index `{idx}` already exists; verifying mapping", Name, indexName );

                var verifyDetail = await VerifyMappingAsync( context, indexName, logger ).ConfigureAwait( false );
                var elapsed = context.TimeProvider.GetElapsedTime( start );
                return StepOutcome.Succeeded( Name, elapsed, verifyDetail );
            }

            if ( context.Options.AssumeIndicesExist )
            {
                var elapsed = context.TimeProvider.GetElapsedTime( start );
                var ex = new OpenSearchLedgerSchemaMismatchException(
                    $"{Name} requires the ledger index `{indexName}` to exist " +
                    $"because AssumeIndicesExist=true. Create it manually with the " +
                    $"required strict mapping (id, runOn, direction, status, appliedBy, " +
                    $"checksum, error, failedStatementIndex) before starting the runner." );
                return StepOutcome.Failed( Name, elapsed, ex, "missing ledger under AssumeIndicesExist" );
            }

            logger.LogInformation( "{step} creating ledger index `{idx}` with strict mapping", Name, indexName );

            var createResponse = await context.Client.LowLevel.Indices.CreateAsync<StringResponse>(
                indexName,
                PostData.String( DefaultMappingJson ),
                ctx: context.CancellationToken
            ).ConfigureAwait( false );

            if ( !createResponse.Success )
            {
                var detail = createResponse.OriginalException?.Message ?? createResponse.Body ?? "Unknown create failure";
                var ex = new OpenSearchProviderException(
                    $"{Name} could not create ledger index `{indexName}`. {detail}",
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
        catch ( OpenSearchLedgerSchemaMismatchException )
        {
            // Allow schema-mismatch exceptions thrown from VerifyMappingAsync to surface as a Failed outcome
            throw;
        }
        catch ( Exception ex )
        {
            var elapsed = context.TimeProvider.GetElapsedTime( start );
            return StepOutcome.Failed( Name, elapsed, new OpenSearchProviderException(
                $"{Name} threw an unexpected exception. {ex.Message}", ex ) );
        }
    }

    private static async Task<string> VerifyMappingAsync( BootstrapContext context, string indexName, ILogger logger )
    {
        var mappingResponse = await context.Client.LowLevel.Indices.GetMappingAsync<StringResponse>(
            indexName, ctx: context.CancellationToken
        ).ConfigureAwait( false );

        if ( !mappingResponse.Success )
        {
            throw new OpenSearchLedgerSchemaMismatchException(
                $"Could not read existing mapping for ledger index `{indexName}`: " +
                (mappingResponse.OriginalException?.Message ?? mappingResponse.Body ?? "unknown error") );
        }

        var doc = JsonNode.Parse( mappingResponse.Body );
        var properties = doc?[indexName]?["mappings"]?["properties"] as JsonObject;

        if ( properties is null )
        {
            throw new OpenSearchLedgerSchemaMismatchException(
                $"Ledger index `{indexName}` exists but has no `mappings.properties` block. " +
                $"Delete the index and let the bootstrapper recreate it, or set AssumeIndicesExist=false." );
        }

        var missing = RequiredFields.Where( f => !properties.ContainsKey( f ) ).ToList();

        if ( missing.Count > 0 )
        {
            throw new OpenSearchLedgerSchemaMismatchException(
                $"Ledger index `{indexName}` is missing required forensic fields: " +
                $"[{string.Join( ", ", missing )}]. Schema is immutable per R-06; recreate the index." );
        }

        logger.LogDebug( "{step} ledger schema verified ({count} required fields present)", "ledger-init", RequiredFields.Length );
        return "verified existing schema";
    }
}
