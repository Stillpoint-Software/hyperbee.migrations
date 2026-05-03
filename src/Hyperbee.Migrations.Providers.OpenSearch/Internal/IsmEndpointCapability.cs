#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal;

// R-21 #3 — ISM endpoint capability resolution.
//
// Modern OpenSearch versions expose the Index State Management plugin under
// `/_plugins/_ism/...`. Older AWS Managed OpenSearch domains (and pre-1.0
// distributions still in production) expose the same APIs under the legacy
// `/_opendistro/_ism/...` prefix. The dispatcher cannot hard-code one path
// without breaking deployments using the other.
//
// At bootstrap, IsmEndpointDetectStep probes the modern path; on 404, it
// probes the legacy path; and on neither, it leaves the capability empty
// (logs a WARN). The dispatcher's CREATE POLICY / APPLY POLICY paths
// consult this capability to choose the prefix at request time.
//
// Lifetime: singleton, written once during bootstrap, read by the
// dispatcher on every ISM-touching statement. Once set, the path is
// immutable for the lifetime of the runner process. Mutability is
// confined to the SetPrefix call below — there is no API to clear or
// override the value at runtime.

public sealed class IsmEndpointCapability
{
    private string? _ismPathPrefix;

    /// <summary>
    /// The ISM API path prefix this cluster supports, e.g. <c>"_plugins/_ism"</c>
    /// (modern) or <c>"_opendistro/_ism"</c> (legacy AWS Managed). Null when
    /// detection has not yet run or both probes failed.
    /// </summary>
    public string? IsmPathPrefix => _ismPathPrefix;

    /// <summary>
    /// True once the capability has been successfully detected.
    /// </summary>
    public bool IsResolved => _ismPathPrefix is not null;

    /// <summary>
    /// Set by IsmEndpointDetectStep at bootstrap. Idempotent — if called
    /// twice with the same value, no change. Different values throw, since
    /// the cluster's ISM surface is fixed for the lifetime of a deployment
    /// and a divergent re-detection signals a logic bug.
    /// </summary>
    internal void SetPrefix( string prefix )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace( prefix );

        var existing = Interlocked.CompareExchange( ref _ismPathPrefix, prefix, null );
        if ( existing is not null && !string.Equals( existing, prefix, StringComparison.Ordinal ) )
        {
            throw new InvalidOperationException(
                $"IsmEndpointCapability already resolved to `{existing}`; refusing to overwrite with `{prefix}`. " +
                "The ISM surface should be detected exactly once at bootstrap." );
        }
    }
}
