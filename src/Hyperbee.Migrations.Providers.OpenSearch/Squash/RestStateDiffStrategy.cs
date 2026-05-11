#nullable enable
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// OpenSearch squash strategy that captures cluster state via REST probes
/// (per ADR-0019). Uses the resolved ISM endpoint prefix
/// (<c>_plugins/_ism</c> or <c>_opendistro/_ism</c>) from the captured
/// <see cref="OpenSearchTopologySignature"/>.
/// </summary>
/// <remarks>
/// <para>
/// V1 happy path (mirrors the Aerospike <c>InfoSnapshotStrategy</c> shape):
/// <list type="number">
///   <item>Validate the context is <see cref="OpenSearchSquashGenerationContext"/>
///         and the descriptor set is non-empty.</item>
///   <item>Capture topology (cluster version, distribution, plugin matrix,
///         ISM endpoint prefix) from the operator's live cluster.</item>
///   <item>Compute bounds + sorted Replaces from descriptors.</item>
///   <item>Capture snapshot B via the injected
///         <see cref="OpenSearchSquashGenerationContext.CaptureSnapshotAsync"/>
///         delegate. The capture function applies migrations through the upper
///         bound against an ephemeral cluster, then calls
///         <see cref="OpenSearchSnapshotCapture.CaptureAsync"/> with the
///         resolved ISM prefix.</item>
///   <item>Canonicalize via <see cref="OpenSearchSnapshotCanonicalizer"/>.</item>
///   <item>Return <see cref="SquashGenerationResult.Generated"/> with content,
///         resolved Replaces, captured topology, and any collected diagnostics.</item>
/// </list>
/// </para>
/// <para>
/// V3.0 emits the section-headered canonical form as <see cref="ContentKind.CanonicalJson"/>
/// content. The canonicalizer's output is the embeddable form -- per Task 2.0
/// spike, no separate "script-form" emission is required because painless
/// preservation makes the canonical form operator-applyable via the CLI's
/// apply path. A richer <c>WITH BODY @path</c> script emission is a Phase 5
/// polish item if operator feedback warrants.
/// </para>
/// <para>
/// <b>MigrationSourceRoot gate</b>: per Aerospike Phase 1 Sev 1 D, the
/// strategy exposes a <see cref="MigrationSourceRoot"/> init-property that
/// (when set) drives a Roslyn-based migration source scan refusing squash
/// generation if any <c>[Migration]</c>-attributed class contains data ops
/// without an explicit annotation. The OpenSearch scanner ships in Task 2.7;
/// until then, the property defaults to null and the gate is skipped.
/// </para>
/// </remarks>
public sealed class RestStateDiffStrategy : ISquashStrategy
{
    public string ProviderId => OpenSearchTopologySignature.ProviderIdValue;

    private readonly OpenSearchSnapshotCanonicalizer _canonicalizer;
    private readonly OpenSearchDataOpClassifier _dataOpClassifier;
    private readonly ILogger<RestStateDiffStrategy> _logger;

    public RestStateDiffStrategy(
        OpenSearchSnapshotCanonicalizer canonicalizer,
        OpenSearchDataOpClassifier dataOpClassifier,
        ILogger<RestStateDiffStrategy>? logger = null )
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException( nameof( canonicalizer ) );
        _dataOpClassifier = dataOpClassifier ?? throw new ArgumentNullException( nameof( dataOpClassifier ) );
        _logger = logger ?? NullLogger<RestStateDiffStrategy>.Instance;
    }

    /// <summary>
    /// Optional path to a directory containing the user's migration source
    /// files. When set, the strategy walks the directory via
    /// <see cref="OpenSearchMigrationSourceScanner"/> and refuses generation
    /// if any <see cref="Migration"/>-derived class in the squash range
    /// matches the data-op heuristic without an explicit
    /// <c>[DataMigration]</c> or <c>[StructuralOnly]</c> annotation
    /// (per ADR-0019 A5). When null, the source-scan refusal gate is skipped.
    /// </summary>
    public string? MigrationSourceRoot { get; init; }

    public async Task<SquashGenerationResult> GenerateAsync(
        ISquashGenerationContext context,
        IReadOnlyList<MigrationDescriptor> descriptors,
        SquashGenerationOptions options,
        CancellationToken cancellationToken = default )
    {
        if ( context is not OpenSearchSquashGenerationContext osContext )
            return new SquashGenerationResult.Failed(
                $"RestStateDiffStrategy requires OpenSearchSquashGenerationContext (received `{context?.GetType().Name ?? "<null>"}`)." );

        if ( descriptors == null || descriptors.Count == 0 )
            return new SquashGenerationResult.Failed( "No migrations supplied for squash." );

        try
        {
            // Source-scan refusal gate (ADR-0019 A5). When MigrationSourceRoot
            // is set, walk the migration assemblies' source tree and refuse
            // generation if any class extending Migration looks like a data
            // op (uses _client.Index*/Update*/Delete*/Bulk*/Reindex* or has
            // a flagged non-determinism call site) without an explicit
            // annotation. Runs BEFORE topology capture so a refused squash
            // does not spin up an unnecessary cluster probe.
            if ( !string.IsNullOrWhiteSpace( MigrationSourceRoot ) )
            {
                var verdicts = OpenSearchMigrationSourceScanner.Scan( MigrationSourceRoot );
                var unannotated = verdicts.Where( v => v.RequiresAnnotation ).ToArray();
                if ( unannotated.Length > 0 )
                {
                    var names = string.Join( ", ", unannotated.Select( v => v.ClassName ) );
                    _logger.LogWarning(
                        "OpenSearch squash refused: {Count} migration class(es) match the data-op heuristic without [DataMigration]/[StructuralOnly] annotation: {Classes}",
                        unannotated.Length, names );
                    return new SquashGenerationResult.Failed(
                        $"OpenSearch squash refused: {unannotated.Length} migration class(es) match the data-op heuristic without an explicit [DataMigration] or [StructuralOnly] annotation (ADR-0019 A5). " +
                        $"Classes: {names}. " +
                        "Annotate each migration explicitly or move it outside the squash range." );
                }
            }

            // Topology: capture from the live cluster so the squash carries
            // the operator's actual version + distribution + plugin matrix.
            // Topology capture also resolves the ISM endpoint prefix that
            // the snapshot capture delegate needs.
            var topology = await OpenSearchTopologySignature
                .CaptureAsync( osContext.Client, cancellationToken )
                .ConfigureAwait( false );

            // Bounds (per options or descriptor range).
            var lowerBound = options?.LowerBound ?? descriptors.Min( d => d.Attribute.Version );
            var upperBound = options?.UpperBound ?? descriptors.Max( d => d.Attribute.Version );

            // The set of versions this squash subsumes.
            var replaces = descriptors
                .Where( d => d.Attribute.Version >= lowerBound && d.Attribute.Version <= upperBound )
                .Select( d => d.Attribute.Version )
                .OrderBy( v => v )
                .ToArray();

            if ( replaces.Length == 0 )
                return new SquashGenerationResult.Failed(
                    $"No migrations in version range [{lowerBound}..{upperBound}]." );

            // Capture snapshot B: apply through the squash's upper bound.
            var captureResult = await osContext.CaptureSnapshotAsync(
                new SnapshotCaptureRequest(
                    Label: "snapshot-B",
                    UpToVersion: upperBound,
                    RequiredTopology: topology ),
                cancellationToken ).ConfigureAwait( false );

            if ( captureResult == null || string.IsNullOrEmpty( captureResult.SnapshotBlob ) )
                return new SquashGenerationResult.Failed(
                    "Snapshot capture returned empty blob. Verify the capture delegate produced section content." );

            // Canonicalize.
            string canonicalized;
            try
            {
                canonicalized = _canonicalizer.Canonicalize( captureResult.SnapshotBlob );
            }
            catch ( MigrationException ex )
            {
                return new SquashGenerationResult.Failed( ex.Message, ex );
            }

            // Diagnostic pass for OpenSearch is intentionally minimal in v3.0.
            // The canonical form is JSON (not statement-form), so there is no
            // per-statement classification to run. Phase 5 polish may add a
            // structural walk that flags painless containing non-deterministic
            // sources (e.g., `Math.random()`, `Instant.now()`) but the data-op
            // classifier is consulted only at the source-scanner gate above.
            var diagnostics = new List<string>();

            // Final canonical-emission pass.
            var emitted = _canonicalizer.EmitScript( canonicalized );

            _logger.LogInformation(
                "OpenSearch squash generated: range [{Lower}..{Upper}], {ReplaceCount} migration(s) replaced, {Length} bytes",
                lowerBound, upperBound, replaces.Length, emitted.Length );

            return new SquashGenerationResult.Generated(
                Content: emitted,
                Kind: ContentKind.CanonicalJson,
                Encoding: ContentEncoding.Utf8,
                Replaces: replaces,
                Diagnostics: diagnostics,
                Topology: topology );
        }
        catch ( OperationCanceledException )
        {
            throw;
        }
        catch ( Exception ex )
        {
            return new SquashGenerationResult.Failed(
                $"RestStateDiffStrategy.GenerateAsync threw: {ex.Message}", ex );
        }
    }
}
