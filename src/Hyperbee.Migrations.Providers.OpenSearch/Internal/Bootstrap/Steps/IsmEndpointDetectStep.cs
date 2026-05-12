#nullable enable
using Microsoft.Extensions.Logging;
using OpenSearch.Net;

// Disambiguate from System.Net.Http.HttpMethod (implicit-using).
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;

// R-21 #3 — Probes the cluster to determine which ISM endpoint prefix it
// exposes:
//
//   /_plugins/_ism/...        — modern OpenSearch (1.0+)
//   /_opendistro/_ism/...     — legacy AWS Managed OpenSearch domains and
//                                pre-1.0 distributions
//
// Probe order is modern-first. On HTTP 404 the step retries against the
// legacy path. On any other failure (5xx, timeout, network) the step
// surfaces the error as Failed so bootstrap halts loudly — silently
// falling back to a wrong prefix would mask cluster-side issues that
// authors need to see.
//
// The probe path: GET `<prefix>/policies` is well-defined on both
// surfaces, idempotent, returns 200 even on a fresh cluster with no
// policies, and requires only read permissions on the ISM REST API.
// IAM-restricted AWS deployments that lack `restapi` access fail here
// with a clear remediation rather than at first CREATE POLICY.

public sealed class IsmEndpointDetectStep : IBootstrapStep
{
    public const string ModernPrefix = "_plugins/_ism";
    public const string LegacyPrefix = "_opendistro/_ism";

    private readonly IsmEndpointCapability _capability;

    public IsmEndpointDetectStep( IsmEndpointCapability capability )
    {
        _capability = capability;
    }

    public string Name => "ism-detect";

    public async Task<StepOutcome> ExecuteAsync( BootstrapContext context )
    {
        var start = context.TimeProvider.GetTimestamp();
        var logger = context.LoggerFactory.CreateLogger<IsmEndpointDetectStep>();
        var ll = context.Client.LowLevel;

        // Modern path first.
        var modernResp = await ll.DoRequestAsync<StringResponse>(
            HttpMethod.GET,
            $"{ModernPrefix}/policies",
            context.CancellationToken ).ConfigureAwait( false );

        if ( modernResp.Success )
        {
            _capability.SetPrefix( ModernPrefix );
            var elapsed = context.TimeProvider.GetElapsedTime( start );
            logger.LogInformation( "{step} resolved to `{prefix}` (modern OpenSearch ISM surface)",
                Name, ModernPrefix );
            return StepOutcome.Succeeded( Name, elapsed, $"resolved to `{ModernPrefix}`" );
        }

        // Modern returned non-success. 404 means the plugin endpoint is
        // unavailable — try legacy. Anything else (5xx, network, auth) is
        // not a "different prefix" signal; bail out so the operator sees
        // the actual cluster issue.
        if ( modernResp.HttpStatusCode != 404 )
        {
            var elapsed = context.TimeProvider.GetElapsedTime( start );
            var detail = modernResp.OriginalException?.Message
                ?? modernResp.Body
                ?? $"HTTP {modernResp.HttpStatusCode}";
            return StepOutcome.Failed( Name, elapsed,
                new OpenSearchProviderException(
                    $"{Name}: probe of `{ModernPrefix}/policies` failed with HTTP {modernResp.HttpStatusCode}. " +
                    $"This is not a 'wrong prefix' signal — the cluster is reachable but the ISM REST API is " +
                    $"refusing the request. On AWS Managed, verify the deploy role has `es:ESHttp*` against " +
                    $"the `_plugins/_ism/*` resource ARNs, OR an `_opendistro_*` policy if the domain is " +
                    $"older. Underlying error: {detail}",
                    modernResp.OriginalException ?? new InvalidOperationException( detail ) ),
                detail );
        }

        // 404 from modern → try legacy.
        logger.LogDebug( "{step} `{modern}` returned 404; probing legacy `{legacy}`",
            Name, ModernPrefix, LegacyPrefix );

        var legacyResp = await ll.DoRequestAsync<StringResponse>(
            HttpMethod.GET,
            $"{LegacyPrefix}/policies",
            context.CancellationToken ).ConfigureAwait( false );

        if ( legacyResp.Success )
        {
            _capability.SetPrefix( LegacyPrefix );
            var elapsed = context.TimeProvider.GetElapsedTime( start );
            logger.LogInformation(
                "{step} resolved to `{prefix}` (legacy opendistro ISM surface — common on older AWS Managed domains)",
                Name, LegacyPrefix );
            return StepOutcome.Succeeded( Name, elapsed, $"resolved to `{LegacyPrefix}`" );
        }

        // Both probes failed. Bootstrap halts; the operator gets the actual
        // path tried and the IAM action required.
        var totalElapsed = context.TimeProvider.GetElapsedTime( start );
        var legacyDetail = legacyResp.OriginalException?.Message
            ?? legacyResp.Body
            ?? $"HTTP {legacyResp.HttpStatusCode}";
        return StepOutcome.Failed( Name, totalElapsed,
            new OpenSearchProviderException(
                $"{Name}: neither `{ModernPrefix}/policies` nor `{LegacyPrefix}/policies` returned success. " +
                $"This usually means: (a) the ISM plugin is not installed (unusual on managed offerings), " +
                $"OR (b) the cluster is too old to expose either path, OR (c) the deploy role lacks ISM " +
                $"REST API permissions. On AWS Managed, the required IAM action is `es:ESHttp*` against " +
                $"`<domain-arn>/_plugins/_ism/*` (or `_opendistro_*` for older domains). " +
                $"Modern probe: HTTP {modernResp.HttpStatusCode}. Legacy probe: {legacyDetail}",
                legacyResp.OriginalException ?? new InvalidOperationException( legacyDetail ) ),
            legacyDetail );
    }
}
