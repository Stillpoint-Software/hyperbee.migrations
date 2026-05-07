using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Postgres squash strategy that uses <c>pg_dump --schema-only</c> output as
/// the source of truth for the squash body (per ADR-0019, IR-CP-4 Red wins).
/// </summary>
/// <remarks>
/// <para>
/// Per Phase 3 reconciliation, mature environments auto-mark the squash row
/// without running its body — the squash body is consumed only on fresh
/// installs (per ADR-0019 ApplyMode.Fresh path). For fresh installs, the
/// canonical snapshot B (full schema at the squash's upper bound) is the
/// correct re-create script.
/// </para>
/// <para>
/// V1 happy path:
/// <list type="number">
///   <item>Capture topology from the operator's live ledger DB.</item>
///   <item>Capture snapshot B (apply migrations through the upper bound) via the
///         injected <see cref="PostgresSquashGenerationContext.CaptureSnapshotAsync"/>.</item>
///   <item>Canonicalize via <see cref="PostgresSnapshotCanonicalizer"/>.</item>
///   <item>Classify each statement; collect non-determinism diagnostics via
///         <see cref="PostgresDataOpClassifier"/>.</item>
///   <item>Emit the canonicalized snapshot B as the squash body, plus
///         <c>setval(...)</c> entries for any captured sequence last_values.</item>
///   <item>Return <see cref="SquashGenerationResult.Generated"/> with content,
///         the resolved Replaces set, and the captured topology.</item>
/// </list>
/// Snapshot A capture (residual head) is reserved for the verification round
/// (Phase 7) and is not emitted by this strategy directly.
/// </para>
/// </remarks>
public sealed class PgDumpSnapshotStrategy : ISquashStrategy
{
    public string ProviderId => PostgresTopologySignature.ProviderIdValue;

    private readonly PostgresSnapshotCanonicalizer _canonicalizer;
    private readonly PostgresDataOpClassifier _dataOpClassifier;

    public PgDumpSnapshotStrategy(
        PostgresSnapshotCanonicalizer canonicalizer,
        PostgresDataOpClassifier dataOpClassifier )
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
        if ( context is not PostgresSquashGenerationContext pgContext )
            return new SquashGenerationResult.Failed(
                $"PgDumpSnapshotStrategy requires PostgresSquashGenerationContext (received `{context?.GetType().Name ?? "<null>"}`)." );

        if ( descriptors == null || descriptors.Count == 0 )
            return new SquashGenerationResult.Failed( "No migrations supplied for squash." );

        try
        {
            // Topology: capture from the live ledger DB so the squash carries
            // the operator's actual server-major / extension set.
            await using var connection = await pgContext.DataSource
                .OpenConnectionAsync( cancellationToken ).ConfigureAwait( false );
            var topology = await PostgresTopologySignature
                .CaptureAsync( connection, cancellationToken ).ConfigureAwait( false );

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
            var captureResult = await pgContext.CaptureSnapshotAsync(
                new SnapshotCaptureRequest(
                    Label: "snapshot-B",
                    UpToVersion: upperBound,
                    RequiredTopology: topology ),
                cancellationToken ).ConfigureAwait( false );

            // Canonicalize. CONCURRENTLY refusal is enforced inside the canonicalizer.
            string canonicalized;
            try
            {
                canonicalized = _canonicalizer.Canonicalize( captureResult.DumpText );
            }
            catch ( MigrationException ex )
            {
                return new SquashGenerationResult.Failed( ex.Message, ex );
            }

            // Per-statement classification + non-determinism scan. Diagnostics
            // populate the Generated.Diagnostics list so the CLI can surface
            // them.
            var diagnostics = new List<string>();
            var statements = PostgresStatementSplitter.Split( canonicalized );
            foreach ( var statementText in statements )
            {
                var ddl = PostgresStatementClassifier.Classify( statementText );
                var dataOp = _dataOpClassifier.Classify( statementText );

                if ( dataOp.EmissionHint != null )
                    diagnostics.Add(
                        $"[{ddl.Kind}] {ddl.SchemaName ?? "(none)"}.{ddl.ObjectName ?? "(none)"}: {dataOp.EmissionHint}" );

                if ( ddl.Kind == PostgresStatementKind.Unknown )
                    diagnostics.Add(
                        $"[Unknown] could not classify statement (length {statementText.Length}); " +
                        "review the squash output before applying. First 80 chars: " +
                        statementText.Substring( 0, Math.Min( 80, statementText.Length ) ).Replace( '\n', ' ' ) );
            }

            // Append sequence setval block (per A6 Phase 6 task spec). One
            // setval per captured sequence, in deterministic name order so the
            // emitted content is byte-stable across re-runs.
            var content = canonicalized;
            if ( captureResult.SequenceLastValues is { Count: > 0 } seqs )
            {
                var setvalBlock = string.Join(
                    Environment.NewLine,
                    seqs.OrderBy( kv => kv.Key, StringComparer.Ordinal )
                        .Select( kv => $"SELECT setval('{kv.Key}', {kv.Value}, true);" ) );
                content = content.TrimEnd( '\n' ) + "\n\n-- Sequence post-emission\n" + setvalBlock + "\n";
            }

            // Final canonical-emission pass (for ADR-0022 script-form output).
            var emitted = _canonicalizer.EmitScript( content );

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
                $"PgDumpSnapshotStrategy.GenerateAsync threw: {ex.Message}", ex );
        }
    }
}
