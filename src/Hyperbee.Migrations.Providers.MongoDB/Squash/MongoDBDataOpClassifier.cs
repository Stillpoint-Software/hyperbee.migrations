using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// MongoDB-flavored <see cref="IDataOpClassifier"/>. Classifies a single
/// statement or call-site string as a data op, structural op, or
/// unclassified (default-deny per ADR-0019 A8).
/// </summary>
/// <remarks>
/// <para>
/// Two input shapes are recognized:
/// <list type="bullet">
///   <item>MongoDB statement form (per <c>MongoStatementParser</c>):
///         <c>CREATE COLLECTION</c>, <c>DROP COLLECTION</c>,
///         <c>CREATE INDEX</c>, <c>CREATE UNIQUE INDEX</c>,
///         <c>DROP INDEX</c> -> structural; <c>INSERT INTO</c> -> data op.</item>
///   <item>.NET call-site expressions: <c>.InsertOne*</c> / <c>.InsertMany*</c>
///         / <c>.UpdateOne*</c> / <c>.UpdateMany*</c> / <c>.ReplaceOne*</c>
///         / <c>.DeleteOne*</c> / <c>.DeleteMany*</c> /
///         <c>.FindOneAndUpdate*</c> / <c>.FindOneAndReplace*</c> /
///         <c>.FindOneAndDelete*</c> / <c>.BulkWrite*</c> -> data op.
///         <c>.Find*</c> / <c>.CountDocuments*</c> / <c>.EstimatedDocumentCount*</c>
///         / <c>.Distinct*</c> / <c>.Aggregate*</c> / <c>.Watch*</c> -> read.
///         <c>.CreateCollection*</c> / <c>.DropCollection*</c> /
///         <c>.RenameCollection*</c> / <c>.ListCollections*</c> /
///         <c>.CreateView*</c> / <c>.Indexes.*</c> sub-client paths -> structural.</item>
/// </list>
/// </para>
/// <para>
/// <b>Receiver-anchoring trade-off:</b> unlike Aerospike (which uses
/// <c>_client</c>) and OpenSearch (which uses <c>_client</c>), MongoDB code
/// typically routes calls through a local <c>collection</c> or <c>db</c>
/// variable rather than the client directly. The MongoDB classifier
/// therefore anchors only on the leading <c>.</c> (method call on
/// something) plus the MongoDB-distinctive method name -- no receiver-name
/// filter. The trade-off: a user class with its own method named
/// <c>InsertOne</c> may false-positive as a data op. Mitigation: the
/// operator annotates with <c>[StructuralOnly]</c> to suppress. False
/// positives are safer than false negatives under the default-deny posture.
/// </para>
/// <para>
/// The non-determinism scan flags .NET sources that produce different
/// output per run -- same catalog as Aerospike and OpenSearch:
/// <c>DateTime.Now</c>, <c>DateTime.UtcNow</c>, <c>DateTime.Today</c>,
/// <c>DateTimeOffset.Now/UtcNow</c>, <c>Guid.NewGuid</c>, <c>new Random()</c>
/// without seed, <c>Random.Shared</c>, <c>Environment.TickCount(64)</c>,
/// <c>Stopwatch.GetTimestamp()</c>.
/// </para>
/// </remarks>
public sealed class MongoDBDataOpClassifier : IDataOpClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // MongoDB statement-form data ops (per Parsers/MongoStatementType).
    private static readonly Regex StatementDml = new(
        @"^\s*INSERT\s+INTO\b",
        Opts );

    // MongoDB statement-form structural ops. Order matters: CREATE UNIQUE
    // INDEX before CREATE INDEX so the more-specific pattern wins.
    private static readonly Regex StatementDdl = new(
        @"^\s*(" +
        @"CREATE\s+COLLECTION|" +
        @"DROP\s+COLLECTION|" +
        @"CREATE\s+UNIQUE\s+INDEX|" +
        @"CREATE\s+INDEX|" +
        @"DROP\s+INDEX" +
        @")\b",
        Opts );

    // Shared method-call tail allowing optional generic type-parameter list
    // before the opening paren (per Phase 2 lesson). MongoDB.Driver methods
    // are generic: `InsertOneAsync<TDoc>(doc)` etc.
    private const string MethodCallTail = @"(?:\s*<[^()]*>)?\s*\(";

    // .NET call-site shapes for IMongoCollection<T> write ops.
    private static readonly Regex CallSiteWrite = new(
        @"\.(" +
        @"InsertOne|InsertOneAsync|InsertMany|InsertManyAsync|" +
        @"UpdateOne|UpdateOneAsync|UpdateMany|UpdateManyAsync|" +
        @"ReplaceOne|ReplaceOneAsync|" +
        @"DeleteOne|DeleteOneAsync|DeleteMany|DeleteManyAsync|" +
        @"FindOneAndUpdate|FindOneAndUpdateAsync|" +
        @"FindOneAndReplace|FindOneAndReplaceAsync|" +
        @"FindOneAndDelete|FindOneAndDeleteAsync|" +
        @"BulkWrite|BulkWriteAsync" +
        @")" + MethodCallTail,
        Opts );

    // .NET call-site shapes for IMongoCollection<T> read ops.
    private static readonly Regex CallSiteRead = new(
        @"\.(" +
        @"Find|FindAsync|FindSync|" +
        @"CountDocuments|CountDocumentsAsync|" +
        @"EstimatedDocumentCount|EstimatedDocumentCountAsync|" +
        @"Distinct|DistinctAsync|" +
        @"Aggregate|AggregateAsync|" +
        @"Watch|WatchAsync" +
        @")" + MethodCallTail,
        Opts );

    // .NET call-site shapes for structural management on IMongoDatabase /
    // IMongoClient / sub-paths (Indexes, Settings).
    private static readonly Regex CallSiteStructural = new(
        @"\.(" +
        @"CreateCollection|CreateCollectionAsync|" +
        @"DropCollection|DropCollectionAsync|" +
        @"RenameCollection|RenameCollectionAsync|" +
        @"CreateView|CreateViewAsync|" +
        @"ListCollections|ListCollectionsAsync|" +
        @"ListCollectionNames|ListCollectionNamesAsync|" +
        @"ListDatabases|ListDatabasesAsync|ListDatabaseNames|ListDatabaseNamesAsync|" +
        @"DropDatabase|DropDatabaseAsync|" +
        @"RunCommand|RunCommandAsync" +
        @")" + MethodCallTail + "|" +
        // Sub-client paths: `collection.Indexes.CreateOneAsync(...)`,
        // `database.GetCollection<T>(...)`, etc. The intermediate property
        // anchors recognize the structural intent without requiring a
        // specific receiver name.
        @"\.(Indexes|Settings)\.\w+" + MethodCallTail,
        Opts );

    // Non-determinism: same .NET catalog as Aerospike and OpenSearch.
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

        // Call-site: structural sub-client paths first (so `.Indexes.CreateOneAsync`
        // is not confused with anything), then writes, then reads.
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
    /// <paramref name="input"/>. Same shape as the Aerospike and OpenSearch
    /// scans; surfaced publicly so the squash codegen can produce determinism
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
