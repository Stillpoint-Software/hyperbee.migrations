using Hyperbee.Migrations.Squash;

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

    public InfoSnapshotStrategy(
        AerospikeSnapshotCanonicalizer canonicalizer,
        AerospikeDataOpClassifier dataOpClassifier )
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException( nameof( canonicalizer ) );
        _dataOpClassifier = dataOpClassifier ?? throw new ArgumentNullException( nameof( dataOpClassifier ) );
    }

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
            // them.
            var diagnostics = new List<string>();
            foreach ( var statementText in AerospikeSnapshotCanonicalizer.SplitStatements( canonicalized ) )
            {
                var classified = AerospikeStatementClassifier.Classify( statementText );
                var dataOp = _dataOpClassifier.Classify( statementText );

                if ( dataOp.EmissionHint != null )
                {
                    var qualifier = QualifiedName( classified );
                    diagnostics.Add( $"[{classified.Kind}] {qualifier}: {dataOp.EmissionHint}" );
                }

                if ( classified.Kind == AerospikeStatementKind.Unknown )
                {
                    var head = statementText.Length > 80
                        ? statementText.Substring( 0, 80 )
                        : statementText;
                    diagnostics.Add(
                        $"[Unknown] could not classify statement (length {statementText.Length}); " +
                        "review the squash output before applying. First 80 chars: " +
                        head.Replace( '\n', ' ' ) );
                }
            }

            // Final canonical-emission pass (for ADR-0022 script-form output).
            var emitted = _canonicalizer.EmitScript( canonicalized );

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
