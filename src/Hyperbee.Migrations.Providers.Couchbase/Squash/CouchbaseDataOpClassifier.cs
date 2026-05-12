using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Couchbase-flavored <see cref="IDataOpClassifier"/>. Classifies a single
/// statement or call-site string as a data op, structural op, or
/// unclassified (default-deny per ADR-0019 A8).
/// </summary>
/// <remarks>
/// <para>
/// Two input shapes are recognized:
/// <list type="bullet">
///   <item>Couchbase N1QL statement form (a superset of the partial
///         <c>StatementParser</c> grammar -- the classifier also detects
///         DML keywords the parser does not consume so resource scripts can
///         be classified without round-tripping through the parser):
///         <c>CREATE [PRIMARY] INDEX</c>, <c>CREATE/DROP BUCKET</c>,
///         <c>CREATE/DROP SCOPE</c>, <c>CREATE/DROP COLLECTION</c>,
///         <c>DROP INDEX</c>, <c>BUILD INDEX</c> -> structural;
///         <c>INSERT INTO</c>, <c>UPSERT INTO</c>, <c>UPDATE</c>,
///         <c>DELETE FROM</c>, <c>MERGE INTO</c> -> data op.</item>
///   <item>.NET call-site expressions on Couchbase SDK objects
///         (collection/bucket/scope/cluster variables): KV writes
///         (<c>UpsertAsync</c>/<c>InsertAsync</c>/<c>ReplaceAsync</c>/
///         <c>RemoveAsync</c>/<c>MutateInAsync</c>/<c>TouchAsync</c>
///         when paired with a write-side variant) -> data op. KV reads
///         (<c>GetAsync</c>/<c>GetAnyReplicaAsync</c>/<c>GetAllReplicasAsync</c>/
///         <c>ExistsAsync</c>/<c>LookupInAsync</c>/<c>GetAndLockAsync</c>/
///         <c>GetAndTouchAsync</c>) -> read.
///         Sub-client management paths
///         (<c>.QueryIndexes.</c>/<c>.Buckets.</c>/<c>.Collections.</c>/
///         <c>.Scopes.</c>/<c>.SearchIndexes.</c>/<c>.AnalyticsIndexes.</c>/
///         <c>.AnalyticsLinks.</c>/<c>.EventingFunctions.</c>/<c>.Users.</c>/
///         <c>.Groups.</c>/<c>.Views.</c>) -> structural.</item>
/// </list>
/// </para>
/// <para>
/// <b>R-P3 Open Question resolution -- parameterized N1QL (.QueryAsync /
/// .AnalyticsQueryAsync):</b> source-only classification cannot reliably
/// extract the SQL value (variables, interpolated strings, builders, helper
/// methods can all carry it). The classifier therefore treats
/// <c>QueryAsync</c> / <c>AnalyticsQueryAsync</c> call-sites as
/// <b>default-deny</b> -- they fall through to the unclassified branch with
/// <see cref="DataOpClassification.RequiresAnnotation"/> set. Operators
/// annotate the migration explicitly with <c>[DataMigration]</c> when the
/// N1QL writes (INSERT/UPSERT/UPDATE/DELETE/MERGE) and
/// <c>[StructuralOnly]</c> when it reads or runs structural DDL. This is the
/// safe posture: false-positive (annotation required when not strictly
/// needed) is cheaper than false-negative (data op silently squashed). The
/// statement-form regex DOES recognize raw DML leading-keywords because
/// resource scripts ship those strings literally -- only the call-site form
/// requires operator annotation.
/// </para>
/// <para>
/// <b>Receiver-anchoring trade-off:</b> Couchbase code routes calls through
/// local <c>collection</c>, <c>bucket</c>, <c>scope</c>, or <c>cluster</c>
/// variables. Like MongoDB, the classifier anchors only on the leading
/// <c>.</c> plus the Couchbase-distinctive method name -- no receiver-name
/// filter. The trade-off: a user class with its own method named
/// <c>UpsertAsync</c> may false-positive as a data op. Mitigation: the
/// operator annotates with <c>[StructuralOnly]</c> to suppress. False
/// positives are safer than false negatives under default-deny.
/// </para>
/// <para>
/// The non-determinism scan flags .NET sources that produce different output
/// per run -- same catalog as Aerospike + OpenSearch + MongoDB.
/// </para>
/// </remarks>
public sealed class CouchbaseDataOpClassifier : IDataOpClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // Couchbase statement-form data ops. Recognizes DML leading-keywords the
    // partial StatementParser doesn't consume; classifier must run on raw
    // resource scripts without round-tripping.
    private static readonly Regex StatementDml = new(
        @"^\s*(" +
        @"INSERT\s+INTO|" +
        @"UPSERT\s+INTO|" +
        @"UPDATE\s+|" +
        @"DELETE\s+FROM|" +
        @"MERGE\s+INTO" +
        @")\b",
        Opts );

    // Couchbase statement-form structural ops. Order matters: more-specific
    // patterns precede generic ones (e.g., CREATE PRIMARY INDEX before
    // CREATE INDEX, BUILD INDEX before BUILD generally).
    private static readonly Regex StatementDdl = new(
        @"^\s*(" +
        @"CREATE\s+PRIMARY\s+INDEX|" +
        @"CREATE\s+INDEX|" +
        @"CREATE\s+BUCKET|" +
        @"CREATE\s+SCOPE|" +
        @"CREATE\s+COLLECTION|" +
        @"DROP\s+PRIMARY\s+INDEX|" +
        @"DROP\s+INDEX|" +
        @"DROP\s+BUCKET|" +
        @"DROP\s+SCOPE|" +
        @"DROP\s+COLLECTION|" +
        @"BUILD\s+INDEX|" +
        @"ALTER\s+INDEX" +
        @")\b",
        Opts );

    // Shared method-call tail allowing optional generic type-parameter list
    // before the opening paren (per Phase 2 cross-provider lesson). Couchbase
    // SDK methods are generic: `collection.UpsertAsync<T>(id, doc)` etc.
    private const string MethodCallTail = @"(?:\s*<[^()]*>)?\s*\(";

    // .NET call-site shapes for Couchbase KV write ops on ICollection.
    private static readonly Regex CallSiteWrite = new(
        @"\.(" +
        @"Upsert|UpsertAsync|" +
        @"Insert|InsertAsync|" +
        @"Replace|ReplaceAsync|" +
        @"Remove|RemoveAsync|" +
        @"MutateIn|MutateInAsync|" +
        @"Append|AppendAsync|" +
        @"Prepend|PrependAsync|" +
        @"Increment|IncrementAsync|" +
        @"Decrement|DecrementAsync|" +
        @"Unlock|UnlockAsync" +
        @")" + MethodCallTail,
        Opts );

    // .NET call-site shapes for Couchbase KV read ops on ICollection.
    private static readonly Regex CallSiteRead = new(
        @"\.(" +
        @"Get|GetAsync|" +
        @"GetAnyReplica|GetAnyReplicaAsync|" +
        @"GetAllReplicas|GetAllReplicasAsync|" +
        @"GetAndLock|GetAndLockAsync|" +
        @"GetAndTouch|GetAndTouchAsync|" +
        @"Touch|TouchAsync|" +
        @"Exists|ExistsAsync|" +
        @"LookupIn|LookupInAsync" +
        @")" + MethodCallTail,
        Opts );

    // .NET call-site shapes for FTS reads.
    private static readonly Regex CallSiteFtsRead = new(
        @"\.(SearchQuery|SearchQueryAsync|SearchAsync)" + MethodCallTail,
        Opts );

    // Sub-client management paths on ICluster / IBucket / IScope. The
    // intermediate property anchor recognizes the structural intent without
    // requiring a specific receiver name.
    private static readonly Regex CallSiteStructural = new(
        @"\.(" +
        @"QueryIndexes|" +
        @"Buckets|" +
        @"Collections|" +
        @"Scopes|" +
        @"SearchIndexes|" +
        @"AnalyticsIndexes|" +
        @"AnalyticsLinks|" +
        @"EventingFunctions|" +
        @"Users|" +
        @"Groups|" +
        @"Views|" +
        @"ViewIndexes" +
        @")\.\w+" + MethodCallTail,
        Opts );

    // Non-determinism: same .NET catalog as the other provider classifiers.
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

        // Statement-form: DML first, then DDL.
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

        // Call-site: structural sub-client paths first (so `.QueryIndexes.CreateAsync`
        // isn't confused with anything), then writes, then reads, then FTS.
        if ( CallSiteStructural.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        if ( CallSiteWrite.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: true,
                RequiresPreservation: true,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        if ( CallSiteRead.IsMatch( input ) || CallSiteFtsRead.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        // Default-deny: caller must annotate. This branch captures
        // .QueryAsync / .AnalyticsQueryAsync per R-P3 OQ resolution -- the
        // SQL is opaque from source-only inspection.
        return new DataOpClassification(
            IsDataOp: false,
            RequiresPreservation: false,
            IsUnclassified: true,
            RequiresAnnotation: true,
            EmissionHint: emissionHint );
    }

    /// <summary>
    /// Returns the set of non-deterministic source patterns detected in
    /// <paramref name="input"/>. Same shape as the other provider scans;
    /// surfaced publicly so the squash codegen can produce determinism
    /// diagnostics independently of full classification.
    /// </summary>
    public static string[] ScanNonDeterminism( string input )
    {
        if ( string.IsNullOrEmpty( input ) )
            return Array.Empty<string>();

        var hits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( Match m in NonDeterminism.Matches( input ) )
            hits.Add( m.Value );

        return hits.ToArray();
    }
}
