using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// MongoDB squash strategy that uses <c>listCollections</c> + per-collection
/// <c>indexes</c> introspection as the source of truth for the squash body
/// (per ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// V1 happy path (mirrors Aerospike + OpenSearch shapes):
/// <list type="number">
///   <item>Validate context type + descriptors.</item>
///   <item>Capture topology (version + FCV + deployment + concerns + storage
///         engine) from the operator's live cluster.</item>
///   <item>Compute bounds + sorted Replaces from descriptors.</item>
///   <item>Capture snapshot B via the injected
///         <see cref="MongoDBSquashGenerationContext.CaptureSnapshotAsync"/>
///         delegate (production wires
///         <see cref="MongoDBSnapshotCapture.CaptureAsync"/>).</item>
///   <item>Canonicalize via <see cref="MongoDBSnapshotCanonicalizer"/>.</item>
///   <item>Return <see cref="SquashGenerationResult.Generated"/> with content,
///         Replaces, captured topology.</item>
/// </list>
/// </para>
/// <para>
/// V3.0 emits the section-headered canonical form as
/// <see cref="ContentKind.CanonicalJson"/> content (NOT statement form --
/// MongoDB structural state includes validators, time-series options, view
/// pipelines that are too rich for the narrow MongoStatementParser
/// grammar). Operators commit the canonical form to source control; the
/// CLI's apply path (Phase 5) walks the JSON sections and reconstructs
/// cluster state via the MongoDB driver. Per Task 2.0 painless spike
/// precedent (opaque-content + structural-canonical).
/// </para>
/// <para>
/// <b>MigrationSourceRoot gate</b>: per Aerospike Phase 1 Sev 1 D / OpenSearch
/// Task 2.7, the strategy exposes a <see cref="MigrationSourceRoot"/> init-
/// property that (when set) drives a Roslyn-based migration source scan
/// refusing squash generation if any <c>[Migration]</c>-attributed class
/// contains data ops without an explicit annotation. The MongoDB scanner
/// ships in a follow-up task; until then, the property defaults to null
/// and the gate is skipped.
/// </para>
/// </remarks>
public sealed class IntrospectionSnapshotStrategy : ISquashStrategy
{
    public string ProviderId => MongoDBTopologySignature.ProviderIdValue;

    private readonly MongoDBSnapshotCanonicalizer _canonicalizer;
    private readonly MongoDBDataOpClassifier _dataOpClassifier;
    private readonly ILogger<IntrospectionSnapshotStrategy> _logger;

    public IntrospectionSnapshotStrategy(
        MongoDBSnapshotCanonicalizer canonicalizer,
        MongoDBDataOpClassifier dataOpClassifier,
        ILogger<IntrospectionSnapshotStrategy> logger = null )
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException( nameof( canonicalizer ) );
        _dataOpClassifier = dataOpClassifier ?? throw new ArgumentNullException( nameof( dataOpClassifier ) );
        _logger = logger ?? NullLogger<IntrospectionSnapshotStrategy>.Instance;
    }

    /// <summary>
    /// Optional path to a directory containing the user's migration source
    /// files. When set, the strategy walks the directory via
    /// <see cref="MongoDBMigrationSourceScanner"/> and refuses generation
    /// if any <see cref="Migration"/>-derived class in the squash range
    /// matches the data-op heuristic without an explicit
    /// <c>[DataMigration]</c> or <c>[StructuralOnly]</c> annotation
    /// (per ADR-0019 A5). When null, the source-scan refusal gate is skipped.
    /// </summary>
    public string MigrationSourceRoot { get; init; }

    public async Task<SquashGenerationResult> GenerateAsync(
        ISquashGenerationContext context,
        IReadOnlyList<MigrationDescriptor> descriptors,
        SquashGenerationOptions options,
        CancellationToken cancellationToken = default )
    {
        if ( context is not MongoDBSquashGenerationContext moContext )
            return new SquashGenerationResult.Failed(
                $"IntrospectionSnapshotStrategy requires MongoDBSquashGenerationContext (received `{context?.GetType().Name ?? "<null>"}`)." );

        if ( descriptors == null || descriptors.Count == 0 )
            return new SquashGenerationResult.Failed( "No migrations supplied for squash." );

        try
        {
            // Source-scan refusal gate (ADR-0019 A5). When MigrationSourceRoot
            // is set, walk the migration assemblies' source tree and refuse
            // generation if any class extending Migration looks like a data op
            // without an explicit annotation. Runs BEFORE topology capture so
            // a refused squash does not waste a cluster probe.
            if ( !string.IsNullOrWhiteSpace( MigrationSourceRoot ) )
            {
                var verdicts = MongoDBMigrationSourceScanner.Scan( MigrationSourceRoot );
                var unannotated = verdicts.Where( v => v.RequiresAnnotation ).ToArray();
                if ( unannotated.Length > 0 )
                {
                    var names = string.Join( ", ", unannotated.Select( v => v.ClassName ) );
                    _logger.LogWarning(
                        "MongoDB squash refused: {Count} migration class(es) match the data-op heuristic without [DataMigration]/[StructuralOnly] annotation: {Classes}",
                        unannotated.Length, names );
                    return new SquashGenerationResult.Failed(
                        $"MongoDB squash refused: {unannotated.Length} migration class(es) match the data-op heuristic without an explicit [DataMigration] or [StructuralOnly] annotation (ADR-0019 A5). " +
                        $"Classes: {names}. " +
                        "Annotate each migration explicitly or move it outside the squash range." );
                }
            }

            // Topology: capture from the live cluster so the squash carries
            // the operator's actual version + FCV + deployment + concerns.
            var topology = await MongoDBTopologySignature
                .CaptureAsync( moContext.Client, moContext.DatabaseName, cancellationToken )
                .ConfigureAwait( false );

            // Bounds (per options or descriptor range).
            var lowerBound = options?.LowerBound ?? descriptors.Min( d => d.Attribute.Version );
            var upperBound = options?.UpperBound ?? descriptors.Max( d => d.Attribute.Version );

            var replaces = descriptors
                .Where( d => d.Attribute.Version >= lowerBound && d.Attribute.Version <= upperBound )
                .Select( d => d.Attribute.Version )
                .OrderBy( v => v )
                .ToArray();

            if ( replaces.Length == 0 )
                return new SquashGenerationResult.Failed(
                    $"No migrations in version range [{lowerBound}..{upperBound}]." );

            // Capture snapshot B: apply through the squash's upper bound.
            var captureResult = await moContext.CaptureSnapshotAsync(
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

            // Diagnostic pass for MongoDB is minimal in v3.0: the canonical
            // form is JSON-section (not statement form), so per-statement
            // classification at strategy time is N/A. Source-scanner gate
            // above is the operator-facing diagnostic.
            var diagnostics = new List<string>();

            var emitted = _canonicalizer.EmitScript( canonicalized );

            _logger.LogInformation(
                "MongoDB squash generated: range [{Lower}..{Upper}], {ReplaceCount} migration(s) replaced, {Length} bytes",
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
                $"IntrospectionSnapshotStrategy.GenerateAsync threw: {ex.Message}", ex );
        }
    }
}
