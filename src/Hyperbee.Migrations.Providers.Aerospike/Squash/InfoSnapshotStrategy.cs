using Aerospike.Client;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Aerospike squash strategy that uses <c>Info.Request("sets/&lt;ns&gt;",
/// "sindex/&lt;ns&gt;")</c> output as the source of truth for the squash body
/// (per ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// Per Phase 3 reconciliation, mature environments auto-mark the squash row
/// without running its body -- the squash body is consumed only on fresh
/// installs (per ADR-0019 ApplyMode.Fresh path). For fresh installs, the
/// canonical snapshot B (full namespace structure at the squash's upper
/// bound) is the correct re-create script.
/// </para>
/// <para>
/// V1 happy path:
/// <list type="number">
///   <item>Validate the context is <see cref="AerospikeSquashGenerationContext"/>
///         and the descriptor set is non-empty.</item>
///   <item>Capture topology from the operator's live cluster.</item>
///   <item>Capture snapshot B (apply migrations through the upper bound) via
///         the injected <see cref="AerospikeSquashGenerationContext.CaptureSnapshotAsync"/>.</item>
///   <item>Canonicalize via <see cref="AerospikeSnapshotCanonicalizer"/>.</item>
///   <item>Classify each emitted statement; collect non-determinism diagnostics
///         via <see cref="AerospikeDataOpClassifier"/> and unclassified-shape
///         warnings via <see cref="AerospikeStatementClassifier"/>.</item>
///   <item>Emit the canonicalized snapshot B as the squash body.</item>
///   <item>Return <see cref="SquashGenerationResult.Generated"/> with content,
///         the resolved Replaces set, and the captured topology.</item>
/// </list>
/// Snapshot A capture (residual head) is reserved for the verification round
/// (Task 1.6) and is not emitted by this strategy directly.
/// </para>
/// <para>
/// V1 scope explicitly excludes UDF capture (per Task 1.3 / 1.4 deferral
/// notes). When Phase 1 ships, operators with UDFs are surfaced a clear
/// diagnostic: "UDF capture not supported in v3.0 squash; carry UDFs forward
/// as separate non-squashed migrations."
/// </para>
/// </remarks>
public sealed class InfoSnapshotStrategy : ISquashStrategy
{
    public string ProviderId => AerospikeTopologySignature.ProviderIdValue;

    private readonly AerospikeSnapshotCanonicalizer _canonicalizer;
    private readonly AerospikeDataOpClassifier _dataOpClassifier;
    private readonly ILogger<InfoSnapshotStrategy> _logger;

    public InfoSnapshotStrategy(
        AerospikeSnapshotCanonicalizer canonicalizer,
        AerospikeDataOpClassifier dataOpClassifier,
        ILogger<InfoSnapshotStrategy> logger = null )
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException( nameof( canonicalizer ) );
        _dataOpClassifier = dataOpClassifier ?? throw new ArgumentNullException( nameof( dataOpClassifier ) );
        _logger = logger ?? NullLogger<InfoSnapshotStrategy>.Instance;
    }

    /// <summary>
    /// Test-only seam for overriding the UDF probe. Production wires the
    /// default which calls <see cref="AerospikeSnapshotCapture.ListUdfs"/>.
    /// </summary>
    internal Func<IAerospikeClient, CancellationToken, IReadOnlyList<string>> UdfProbe { get; init; }
        = AerospikeSnapshotCapture.ListUdfs;

    /// <summary>
    /// Optional path to a directory containing the user's migration source
    /// files. When set, the strategy walks the directory via
    /// <see cref="AerospikeMigrationSourceScanner"/> and refuses generation
    /// if any <see cref="Migration"/>-derived class in the squash range
    /// matches the data-op heuristic without an explicit
    /// <c>[DataMigration]</c> or <c>[StructuralOnly]</c> annotation
    /// (per ADR-0019 A5).
    /// </summary>
    /// <remarks>
    /// When null (the default), the source-scan refusal gate is skipped --
    /// the strategy still produces a valid snapshot but operators are
    /// responsible for verifying their migrations don't contain unannotated
    /// data ops. The CLI wires this property to the operator's migration
    /// assembly source root in Phase 5.
    /// </remarks>
    public string MigrationSourceRoot { get; init; }

    public async Task<SquashGenerationResult> GenerateAsync(
        ISquashGenerationContext context,
        IReadOnlyList<MigrationDescriptor> descriptors,
        SquashGenerationOptions options,
        CancellationToken cancellationToken = default )
    {
        if ( context is not AerospikeSquashGenerationContext asContext )
            return new SquashGenerationResult.Failed(
                $"InfoSnapshotStrategy requires AerospikeSquashGenerationContext (received `{context?.GetType().Name ?? "<null>"}`)." );

        if ( descriptors == null || descriptors.Count == 0 )
            return new SquashGenerationResult.Failed( "No migrations supplied for squash." );

        try
        {
            // UDF refusal gate (per Task 1.5 deferral; ADR-0019 prefers
            // refuse-with-diagnostic over silent state loss). V3.0 squash
            // codegen does NOT round-trip Lua UDFs through the canonical
            // output; if the live cluster has any installed UDFs the
            // squash would silently drop them on fresh-install replay.
            // Refuse with a clear diagnostic naming the offending modules
            // so the operator can either remove UDF migrations from the
            // squash range or carry them forward as separate non-squashed
            // migrations.
            var udfs = UdfProbe( asContext.Client, cancellationToken );
            if ( udfs.Count > 0 )
            {
                _logger.LogWarning(
                    "Aerospike squash refused: cluster has {Count} installed UDF module(s); UDF capture is not supported in v3.0. Modules: {Udfs}",
                    udfs.Count, string.Join( ", ", udfs ) );
                return new SquashGenerationResult.Failed(
                    $"Aerospike squash refused: cluster has {udfs.Count} installed Lua UDF module(s) which v3.0 squash codegen cannot round-trip. " +
                    $"Modules: {string.Join( ", ", udfs )}. " +
                    "Carry UDFs forward as separate non-squashed migrations (place UDF-creating migrations outside the squash range), " +
                    "or remove them before squashing." );
            }

            // Source-scan refusal gate (ADR-0019 A5). When MigrationSourceRoot
            // is set, walk the migration assemblies' source tree and refuse
            // generation if any class extending Migration looks like a data
            // op (uses _client.Put/Delete/Operate/Touch or has a flagged
            // non-determinism call site) without an explicit annotation.
            if ( !string.IsNullOrWhiteSpace( MigrationSourceRoot ) )
            {
                var verdicts = AerospikeMigrationSourceScanner.Scan( MigrationSourceRoot );
                var unannotated = verdicts.Where( v => v.RequiresAnnotation ).ToArray();
                if ( unannotated.Length > 0 )
                {
                    var names = string.Join( ", ", unannotated.Select( v => v.ClassName ) );
                    _logger.LogWarning(
                        "Aerospike squash refused: {Count} migration class(es) match the data-op heuristic without [DataMigration]/[StructuralOnly] annotation: {Classes}",
                        unannotated.Length, names );
                    return new SquashGenerationResult.Failed(
                        $"Aerospike squash refused: {unannotated.Length} migration class(es) match the data-op heuristic without an explicit [DataMigration] or [StructuralOnly] annotation (ADR-0019 A5). " +
                        $"Classes: {names}. " +
                        "Annotate each migration explicitly or move it outside the squash range." );
                }
            }

            // Topology: capture from the live cluster so the squash carries
            // the operator's actual server-major / namespace settings.
            var topology = await AerospikeTopologySignature
                .CaptureAsync( asContext.Client, asContext.Namespace, cancellationToken )
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
            var captureResult = await asContext.CaptureSnapshotAsync(
                new SnapshotCaptureRequest(
                    Label: "snapshot-B",
                    UpToVersion: upperBound,
                    RequiredTopology: topology ),
                cancellationToken ).ConfigureAwait( false );

            if ( captureResult == null || string.IsNullOrEmpty( captureResult.SnapshotBlob ) )
                return new SquashGenerationResult.Failed(
                    "Snapshot capture returned empty blob. Verify the capture delegate produced [sets]/[sindex] section content." );

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

            // Per-statement classification + non-determinism scan. Diagnostics
            // populate the Generated.Diagnostics list so the CLI can surface
            // them; the strategy also logs each diagnostic at Warning so
            // they're visible without consumers having to walk Generated.
            var diagnostics = new List<string>();
            foreach ( var statementText in AerospikeSnapshotCanonicalizer.SplitStatements( canonicalized ) )
            {
                var classified = AerospikeStatementClassifier.Classify( statementText );
                var dataOp = _dataOpClassifier.Classify( statementText );

                if ( dataOp.EmissionHint != null )
                {
                    var qualifier = QualifiedName( classified );
                    var diagnostic = $"[{classified.Kind}] {qualifier}: {dataOp.EmissionHint}";
                    diagnostics.Add( diagnostic );
                    _logger.LogWarning( "Aerospike squash diagnostic: {Diagnostic}", diagnostic );
                }

                if ( classified.Kind == AerospikeStatementKind.Unknown )
                {
                    var head = statementText.Length > 80
                        ? statementText.Substring( 0, 80 )
                        : statementText;
                    var diagnostic =
                        $"[Unknown] could not classify statement (length {statementText.Length}); " +
                        "review the squash output before applying. First 80 chars: " +
                        head.Replace( '\n', ' ' );
                    diagnostics.Add( diagnostic );
                    _logger.LogWarning( "Aerospike squash diagnostic: {Diagnostic}", diagnostic );
                }
            }

            // Final canonical-emission pass (for ADR-0022 script-form output).
            var emitted = _canonicalizer.EmitScript( canonicalized );

            _logger.LogInformation(
                "Aerospike squash generated: range [{Lower}..{Upper}], {ReplaceCount} migration(s) replaced, {Length} bytes, {DiagCount} diagnostic(s)",
                lowerBound, upperBound, replaces.Length, emitted.Length, diagnostics.Count );

            return new SquashGenerationResult.Generated(
                Content: emitted,
                Kind: ContentKind.SqlText,
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
                $"InfoSnapshotStrategy.GenerateAsync threw: {ex.Message}", ex );
        }
    }

    private static string QualifiedName( ClassifiedStatement c )
    {
        var ns = c.Namespace ?? "(none)";
        var name = c.ObjectName ?? c.SetName ?? "(none)";
        return $"{ns}.{name}";
    }
}
