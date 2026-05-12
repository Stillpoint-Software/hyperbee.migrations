using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Aerospike-flavored <see cref="IDataOpClassifier"/>. Classifies a single
/// statement or call-site string as a data op, structural op, or unclassified
/// (default-deny per ADR-0019 A8).
/// </summary>
/// <remarks>
/// <para>
/// Two input shapes are recognized:
/// <list type="bullet">
///   <item>Aerospike statement form (per ADR-0022 script-form resources):
///         <c>CREATE INDEX</c>, <c>DROP INDEX</c>, <c>CREATE SET</c> -- all
///         structural; <c>INSERT INTO</c>, <c>DELETE FROM</c> -- data ops.</item>
///   <item>.NET call-site expressions extracted by the migration source
///         scanner: <c>_client.Put(...)</c>, <c>_client.Delete(...)</c>,
///         <c>_client.Operate(...)</c>, <c>_client.Touch(...)</c> -- data ops;
///         <c>_client.Get*</c>, <c>_client.Exists*</c>, <c>_client.Query*</c>
///         -- reads (not data); <c>Info.Request</c>, <c>CreateIndex</c>,
///         <c>DropIndex</c>, namespace/set management -- structural.</item>
/// </list>
/// Inputs matching neither shape return <see cref="DataOpClassification.IsUnclassified"/>
/// with <see cref="DataOpClassification.RequiresAnnotation"/> set; the operator
/// must annotate the migration with <c>[DataMigration]</c> or
/// <c>[StructuralOnly]</c> before squash.
/// </para>
/// <para>
/// The non-determinism scan flags .NET sources that produce different output
/// per run: <c>DateTime.Now</c>, <c>DateTime.UtcNow</c>, <c>DateTimeOffset.Now</c>,
/// <c>DateTimeOffset.UtcNow</c>, <c>Guid.NewGuid()</c>, <c>new Random()</c>
/// (without a seed), <c>Random.Shared</c>, <c>Environment.TickCount</c>, and
/// <c>Stopwatch.GetTimestamp()</c>. Detected non-determinism populates
/// <see cref="DataOpClassification.EmissionHint"/>.
/// </para>
/// </remarks>
public sealed class AerospikeDataOpClassifier : IDataOpClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // Aerospike statement-form data ops (per Parsers/AerospikeStatementType).
    private static readonly Regex StatementDml = new(
        @"^\s*(INSERT\s+INTO|DELETE\s+FROM)\b",
        Opts );

    // Aerospike statement-form structural ops.
    private static readonly Regex StatementDdl = new(
        @"^\s*(CREATE\s+INDEX|DROP\s+INDEX|CREATE\s+SET|DROP\s+SET|CREATE\s+UDF|DROP\s+UDF)\b",
        Opts );

    // .NET call-site shapes for Aerospike client write ops. The receiver
    // anchor (`\b_?client\.`) keeps the matcher from firing on the same verb
    // appearing inside argument lists -- e.g., `Operation.Put(bin)` passed as
    // an argument to `_client.Operate` must NOT classify as a write. The
    // codebase convention is `_client` or `client` as the receiver identifier.
    private static readonly Regex CallSiteWrite = new(
        @"\b_?client\.(Put|Delete|Touch)\s*\(",
        Opts );

    // Operate is a write when its operations list contains write ops,
    // read-only otherwise. Demand explicit annotation rather than guess.
    private static readonly Regex CallSiteOperate = new(
        @"\b_?client\.Operate\s*\(",
        Opts );

    // .NET call-site shapes for Aerospike read ops.
    private static readonly Regex CallSiteRead = new(
        @"\b_?client\.(Get|GetHeader|Exists|Query|ScanAll|ScanNode|BatchGet)\s*\(",
        Opts );

    // Structural management call sites: receiver-anchored client management
    // verbs OR the static `Info.Request` / `Info.Reset` entry points.
    private static readonly Regex CallSiteStructural = new(
        @"\b_?client\.(CreateIndex|DropIndex|CreateIndexAsync|DropIndexAsync|RegisterUdf|RemoveUdf)\s*\(|" +
        @"\bInfo\.(Request|Reset)\s*\(",
        Opts );

    // Non-determinism: .NET sources that vary per run. Word-boundary anchors
    // prevent `Foo.DateTime.Now` from matching a custom path; the . in the
    // pattern is the member-access dot.
    private static readonly Regex NonDeterminism = new(
        @"(DateTime\.Now|DateTime\.UtcNow|DateTimeOffset\.Now|DateTimeOffset\.UtcNow|" +
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

        if ( CallSiteWrite.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: true,
                RequiresPreservation: true,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        // Operate can be read OR write; demand explicit annotation rather than
        // guess at the operations list shape from raw text.
        if ( CallSiteOperate.IsMatch( input ) )
        {
            var operateHint = emissionHint != null
                ? emissionHint + "; Operate call site requires explicit annotation (read vs write)"
                : "Operate call site requires explicit annotation (read vs write)";

            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
                IsUnclassified: true,
                RequiresAnnotation: true,
                EmissionHint: operateHint );
        }

        if ( CallSiteStructural.IsMatch( input ) )
        {
            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
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
    /// <paramref name="input"/>. Empty when no non-determinism is present.
    /// Exposed so the squash codegen can surface diagnostics independently of
    /// full classification (e.g., during the C12 determinism gate).
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
