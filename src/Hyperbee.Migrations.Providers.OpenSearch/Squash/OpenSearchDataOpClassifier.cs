#nullable enable
using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// OpenSearch-flavored <see cref="IDataOpClassifier"/>. Classifies a single
/// statement or call-site string as a data op, structural op, or unclassified
/// (default-deny per ADR-0019 A8).
/// </summary>
/// <remarks>
/// <para>
/// Two input shapes are recognized:
/// <list type="bullet">
///   <item>OpenSearch statement form (per the existing
///         <c>OpenSearchStatementParser</c>): <c>CREATE INDEX</c>,
///         <c>CREATE TEMPLATE</c>, <c>CREATE COMPONENT</c>, <c>CREATE POLICY</c>,
///         <c>APPLY POLICY</c>, <c>DETACH POLICY</c>,
///         <c>UPDATE MAPPING</c>, <c>UPDATE SETTINGS</c>,
///         <c>ALIAS SWAP</c>/<c>ADD</c>/<c>REMOVE</c>, <c>DROP</c> family,
///         <c>REFRESH</c>, <c>WAIT FOR HEALTH</c>, <c>WAIT UNTIL TASK</c> ->
///         structural. <c>REINDEX FROM</c>/<c>TO</c> and <c>MIGRATE INDEX</c>
///         are DATA OPS (they move documents).</item>
///   <item>.NET call-site expressions (receiver-anchored
///         <c>_?client.&lt;Verb&gt;(</c> per the Aerospike lesson):
///         <c>Index*</c>, <c>Update*</c>, <c>UpdateByQuery*</c>,
///         <c>Delete*</c> (where the trailing 's' / sub-client path is
///         excluded), <c>DeleteByQuery*</c>, <c>Bulk*</c>, <c>Reindex*</c>
///         -> data op. <c>Get*</c>, <c>Search*</c>, <c>MultiGet*</c>,
///         <c>Count*</c>, <c>Exists*</c>, <c>Source*</c> -> read.
///         Sub-client paths (<c>_client.Indices.</c>, <c>_client.Cluster.</c>,
///         <c>_client.Ingest.</c>, <c>_client.Cat.</c>) -> structural.</item>
/// </list>
/// Inputs matching neither shape return
/// <see cref="DataOpClassification.IsUnclassified"/> with
/// <see cref="DataOpClassification.RequiresAnnotation"/> set.
/// </para>
/// <para>
/// The non-determinism scan flags .NET sources that produce different output
/// per run (same catalog as Aerospike): <c>DateTime.Now</c>,
/// <c>DateTime.UtcNow</c>, <c>DateTime.Today</c>, <c>DateTimeOffset.Now</c>,
/// <c>DateTimeOffset.UtcNow</c>, <c>Guid.NewGuid()</c>, <c>new Random()</c>
/// without seed, <c>Random.Shared</c>, <c>Environment.TickCount(64)</c>,
/// <c>Stopwatch.GetTimestamp()</c>. Detected non-determinism populates
/// <see cref="DataOpClassification.EmissionHint"/>.
/// </para>
/// </remarks>
public sealed class OpenSearchDataOpClassifier : IDataOpClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // OpenSearch statement-form data ops (per Internal/Grammar/OpenSearchStatementParser).
    // REINDEX moves documents; MIGRATE INDEX is the orchestrated reindex shape.
    private static readonly Regex StatementDml = new(
        @"^\s*(REINDEX\s+FROM|MIGRATE\s+INDEX)\b",
        Opts );

    // OpenSearch statement-form structural ops. Order matters: more-specific
    // patterns precede generic ones (e.g., CREATE COMPONENT before CREATE).
    private static readonly Regex StatementDdl = new(
        @"^\s*(" +
        @"CREATE\s+(INDEX|TEMPLATE|COMPONENT|POLICY)|" +
        @"DROP\s+(INDEX|TEMPLATE|COMPONENT|POLICY)|" +
        @"APPLY\s+POLICY|" +
        @"DETACH\s+POLICY|" +
        @"UPDATE\s+(MAPPING|SETTINGS)|" +
        @"ALIAS\s+(SWAP|ADD|REMOVE)|" +
        @"REFRESH\b|" +
        @"WAIT\s+(FOR\s+HEALTH|UNTIL\s+TASK)\b" +
        @")",
        Opts );

    // .NET call-site shapes for OpenSearch client write ops. Receiver anchor
    // requires `client` or `_client` immediately before the verb (matches the
    // codebase convention).
    //
    // The trailing `MethodCallTail` allows an optional generic type-parameter
    // list between the method name and the opening paren -- OpenSearch.Client
    // exposes most ops as generic methods (`IndexAsync<T>(...)`), and a naive
    // `\s*\(` anchor misses every typed call site.
    //
    // Async / sync / -Document variants all enumerate explicitly rather than
    // using a wildcard, so we never accidentally match a verb prefix shared
    // by a structural method (e.g., `IndexExists` is a HEAD probe, not a
    // write -- it's enumerated in the read pattern below).
    private const string MethodCallTail = @"(?:\s*<[^()]*>)?\s*\(";

    private static readonly Regex CallSiteWrite = new(
        @"\b_?client\.(" +
        @"Index|IndexAsync|IndexDocument|IndexDocumentAsync|" +
        @"Update|UpdateAsync|UpdateByQuery|UpdateByQueryAsync|" +
        @"Delete|DeleteAsync|DeleteByQuery|DeleteByQueryAsync|" +
        @"Bulk|BulkAsync|BulkAll" +
        @")" + MethodCallTail,
        Opts );

    // Reindex variants are data ops (document movement). Separate regex so
    // the diagnostic can name "reindex" specifically.
    private static readonly Regex CallSiteReindex = new(
        @"\b_?client\.(Reindex|ReindexAsync|ReindexOnServer|ReindexOnServerAsync)" + MethodCallTail,
        Opts );

    // .NET call-site shapes for OpenSearch client read ops.
    private static readonly Regex CallSiteRead = new(
        @"\b_?client\.(" +
        @"Get|GetAsync|MultiGet|MultiGetAsync|" +
        @"Search|SearchAsync|SearchTemplate|SearchTemplateAsync|" +
        @"Count|CountAsync|Exists|ExistsAsync|" +
        @"Source|SourceAsync|SourceExists|SourceExistsAsync|" +
        @"Scroll|ScrollAsync|ClearScroll|ClearScrollAsync|" +
        @"IndexExists|IndexExistsAsync" +
        @")" + MethodCallTail,
        Opts );

    // Sub-client paths on the OpenSearch client: structural management.
    // Matches `_client.Indices.<anything>`, `_client.Cluster.<anything>`,
    // `_client.Ingest.<anything>`, `_client.Cat.<anything>`. The intermediate
    // property anchors prevent confusion with data-op verbs that share name
    // prefixes (Indices vs Index).
    private static readonly Regex CallSiteStructural = new(
        @"\b_?client\.(Indices|Cluster|Ingest|Cat|Tasks|Snapshot|Security|Nodes)\.\w+" + MethodCallTail,
        Opts );

    // Non-determinism: same .NET catalog as the Aerospike classifier.
    // Same anchoring rationale: word-boundary anchors prevent `Foo.DateTime.Now`
    // path from matching a custom path that shadows the BCL identifier.
    private static readonly Regex NonDeterminism = new(
        @"(DateTime\.Now|DateTime\.UtcNow|DateTime\.Today|" +
        @"DateTimeOffset\.Now|DateTimeOffset\.UtcNow|" +
        @"Guid\.NewGuid|Random\.Shared|new\s+Random\s*\(\s*\)|" +
        @"Environment\.TickCount(?:64)?|Stopwatch\.GetTimestamp)",
        Opts );

    public DataOpClassification Classify( string statementOrCallSite )
    {
        ArgumentNullException.ThrowIfNull( statementOrCallSite );

        var input = statementOrCallSite;

        var nonDetHits = ScanNonDeterminism( input );
        var emissionHint = nonDetHits.Length > 0
            ? $"non-deterministic call(s) detected: {string.Join( ", ", nonDetHits )}"
            : null;

        // Statement-form: DML first (REINDEX/MIGRATE INDEX), then DDL.
        if ( StatementDml.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: true,
                RequiresPreservation: true,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        if ( StatementDdl.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        // Call-site form: structural sub-client paths first (so
        // `_client.Indices.Create(` doesn't confuse with `_client.Index(`),
        // then data ops, then reads.
        if ( CallSiteStructural.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        if ( CallSiteWrite.IsMatch( input ) || CallSiteReindex.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: true,
                RequiresPreservation: true,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        if ( CallSiteRead.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        // Default-deny: caller must annotate.
        return new DataOpClassification(
            IsDataOp: false,
            RequiresPreservation: false,
            IsUnclassified: true,
            RequiresAnnotation: true,
            EmissionHint: emissionHint );
    }

    /// <summary>
    /// Returns the set of non-deterministic source patterns detected in
    /// <paramref name="input"/>. Same shape as the Aerospike scan; surfaced
    /// publicly so the squash codegen can produce determinism diagnostics
    /// independently of full classification.
    /// </summary>
    public static string[] ScanNonDeterminism( string? input )
    {
        if ( string.IsNullOrEmpty( input ) )
            return Array.Empty<string>();

        var hits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( Match m in NonDeterminism.Matches( input ) )
            hits.Add( m.Value );

        return hits.ToArray();
    }
}
