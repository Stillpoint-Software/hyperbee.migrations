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
              "failedStatementIndex": { "type": "integer" },
              "kind":                 { "type": "byte" },
              "replaces":             { "type": "long" }
            }
          }
        }
        """;

    // Additive mapping update applied to existing v2-era ledger indices so
    // they accept the new kind/replaces fields under strict dynamic. Adding
    // properties to a strict mapping via PUT _mapping is supported and
    // idempotent (no-op if the fields already exist with the same shape).
    private const string MappingPatchJson = """
        {
          "properties": {
            "kind":     { "type": "byte" },
            "replaces": { "type": "long" }
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

                // v3 additive: ensure kind/replaces fields exist on pre-existing v2 indices.
                // Idempotent under repeated runs; failure is non-fatal in the AssumeIndicesExist
                // path (operator may lack indices:admin/mapping under tight IAM, e.g. AWS Managed).
                await PatchMappingAsync( context, indexName, logger ).ConfigureAwait( false );

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

            StringResponse createResponse;
            try
            {
                createResponse = await context.Client.LowLevel.Indices.CreateAsync<StringResponse>(
                    indexName,
                    PostData.String( DefaultMappingJson ),
                    ctx: context.CancellationToken
                ).ConfigureAwait( false );
            }
            catch ( OpenSearchClientException ex ) when ( IsResourceAlreadyExists( ex.Response ) )
            {
                // TOCTOU race: another runner created the index between our Exists()
                // check and Create(). Verify the mapping and treat as success.
                logger.LogDebug( "{step} ledger index `{idx}` created concurrently by another runner; verifying mapping", Name, indexName );
                var verifyDetail = await VerifyMappingAsync( context, indexName, logger ).ConfigureAwait( false );
                var raceElapsed = context.TimeProvider.GetElapsedTime( start );
                return StepOutcome.Succeeded( Name, raceElapsed, $"{verifyDetail} (raced)" );
            }

            if ( !createResponse.Success )
            {
                if ( IsResourceAlreadyExists( createResponse ) )
                {
                    logger.LogDebug( "{step} ledger index `{idx}` created concurrently by another runner; verifying mapping", Name, indexName );
                    var verifyDetail = await VerifyMappingAsync( context, indexName, logger ).ConfigureAwait( false );
                    var raceElapsed = context.TimeProvider.GetElapsedTime( start );
                    return StepOutcome.Succeeded( Name, raceElapsed, $"{verifyDetail} (raced)" );
                }

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

    // Idempotent additive mapping update for v2-era indices that predate the
    // kind/replaces fields. PUT _mapping with a property block is a no-op
    // when the field already exists with the same type. The verification
    // (RequiredFields) intentionally does NOT include kind/replaces — pre-v3
    // indices that haven't been patched yet still satisfy the strict R-06
    // forensic schema check; the patch happens after verify in the same step.
    private static async Task PatchMappingAsync( BootstrapContext context, string indexName, ILogger logger )
    {
        try
        {
            var patchResponse = await context.Client.LowLevel.Indices.PutMappingAsync<StringResponse>(
                indexName,
                PostData.String( MappingPatchJson ),
                ctx: context.CancellationToken
            ).ConfigureAwait( false );

            if ( !patchResponse.Success )
            {
                // Tight IAM (AWS Managed under restricted role) may forbid mapping updates;
                // log at warning, do not fail the step. v3 writes will surface the error
                // at write time on indices that haven't been patched.
                var detail = patchResponse.OriginalException?.Message ?? patchResponse.Body ?? "unknown patch failure";
                logger.LogWarning(
                    "{step} could not apply v3 ledger mapping patch (kind/replaces) to `{idx}`: {detail}",
                    "ledger-init", indexName, detail );
            }
            else
            {
                logger.LogDebug( "{step} v3 ledger mapping patch applied (kind/replaces) to `{idx}`", "ledger-init", indexName );
            }
        }
        catch ( OperationCanceledException )
        {
            throw;
        }
        catch ( Exception ex )
        {
            logger.LogWarning( ex, "{step} v3 ledger mapping patch threw; continuing", "ledger-init" );
        }
    }

    // Detects the OpenSearch-specific 400 body that signals a TOCTOU race
    // between Exists() and Create() — another runner won. Inspect the body
    // string rather than the status code alone because OS reuses 400 for
    // genuine bad-request shapes (malformed mapping, invalid settings).
    private static bool IsResourceAlreadyExists( IApiCallDetails? response )
    {
        if ( response is null || response.HttpStatusCode != 400 )
            return false;

        var body = response.ResponseBodyInBytes is { Length: > 0 } bytes
            ? System.Text.Encoding.UTF8.GetString( bytes )
            : null;

        return body is not null && body.Contains( "resource_already_exists_exception", StringComparison.Ordinal );
    }

    private static bool IsResourceAlreadyExists( StringResponse response )
    {
        if ( response.HttpStatusCode != 400 )
            return false;

        return !string.IsNullOrEmpty( response.Body )
            && response.Body.Contains( "resource_already_exists_exception", StringComparison.Ordinal );
    }
}
