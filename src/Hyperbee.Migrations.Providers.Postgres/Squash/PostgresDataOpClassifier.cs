using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Postgres-flavored <see cref="IDataOpClassifier"/>. Classifies a single SQL
/// statement as a data op (DML), structural op (DDL), or unclassified
/// (default-deny per ADR-0019 A8).
/// </summary>
/// <remarks>
/// <para>
/// V1 scope: classifies <em>statement text</em> — i.e., the verbatim SQL string
/// already in hand at snapshot-diff time. The Roslyn-based migration source
/// scanner that walks user code and enforces mandatory <c>[DataMigration]</c>
/// annotations (per ADR-0019 A5) is a Phase 6 follow-up; the contract surface
/// supports both via the <see cref="DataOpClassification.RequiresAnnotation"/>
/// flag, but v1 emits it only when the statement text exhibits non-determinism
/// or is otherwise ambiguous.
/// </para>
/// <para>
/// The non-determinism scan flags Postgres calls that produce different output
/// per run: <c>now()</c>, <c>current_timestamp</c>, <c>random()</c>,
/// <c>gen_random_uuid()</c>, <c>uuid_generate_*</c>,
/// <c>clock_timestamp()</c>, <c>statement_timestamp()</c>, <c>txid_current()</c>,
/// and <c>pg_backend_pid()</c>. Detected non-determinism populates
/// <see cref="DataOpClassification.EmissionHint"/> with the offending function
/// name(s) so the squash CLI can surface a clear diagnostic.
/// </para>
/// </remarks>
public sealed class PostgresDataOpClassifier : IDataOpClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // DML — produces, mutates, or removes user data.
    private static readonly Regex DmlVerb = new(
        @"^\s*(INSERT\s+INTO|UPDATE\s+|DELETE\s+FROM|TRUNCATE\s+|MERGE\s+INTO|COPY\s+\w+\s+FROM|SELECT\s+.*\s+INTO\s+|CREATE\s+TABLE\s+\w+\s+AS\s+SELECT)",
        Opts | RegexOptions.Singleline );

    // DDL — structural shape changes. Order matters: the more-specific
    // CTAS pattern above precedes generic CREATE TABLE.
    private static readonly Regex DdlVerb = new(
        @"^\s*(CREATE\s+(SCHEMA|TABLE|INDEX|VIEW|MATERIALIZED\s+VIEW|TYPE|DOMAIN|SEQUENCE|FUNCTION|PROCEDURE|TRIGGER|POLICY|ROLE|USER|GROUP|EXTENSION)|" +
        @"ALTER\s+(SCHEMA|TABLE|INDEX|VIEW|MATERIALIZED\s+VIEW|TYPE|DOMAIN|SEQUENCE|FUNCTION|PROCEDURE|TRIGGER|POLICY|ROLE|USER|GROUP|EXTENSION)|" +
        @"DROP\s+(SCHEMA|TABLE|INDEX|VIEW|MATERIALIZED\s+VIEW|TYPE|DOMAIN|SEQUENCE|FUNCTION|PROCEDURE|TRIGGER|POLICY|ROLE|USER|GROUP|EXTENSION)|" +
        @"COMMENT\s+ON\s+|GRANT\s+|REVOKE\s+|SET\s+|RESET\s+)",
        Opts );

    // DO $$ ... $$ — opaque procedural block. May contain DML; classify as
    // RequiresPreservation since we can't statically dissect arbitrary plpgsql.
    private static readonly Regex DoBlock = new( @"^\s*DO\s+\$", Opts );

    // Non-determinism scan: function-call shapes that can vary per invocation.
    // Word-boundary anchors ensure `pg_now()` in a custom function name doesn't
    // false-positive on `now()`.
    private static readonly Regex NonDeterminism = new(
        @"\b(now|current_timestamp|current_time|current_date|localtimestamp|localtime|" +
        @"random|gen_random_uuid|uuid_generate_v[1345]|" +
        @"clock_timestamp|statement_timestamp|transaction_timestamp|" +
        @"txid_current|pg_backend_pid|pg_current_xact_id)\s*\(",
        Opts );

    public DataOpClassification Classify( string statementOrCallSite )
    {
        ArgumentNullException.ThrowIfNull( statementOrCallSite );

        var statement = statementOrCallSite;

        var nonDetHits = ScanNonDeterminism( statement );
        var emissionHint = nonDetHits.Length > 0
            ? $"non-deterministic call(s) detected: {string.Join( ", ", nonDetHits )}"
            : null;

        // DO blocks: opaque plpgsql; treat as data-op-bearing for safety.
        if ( DoBlock.IsMatch( statement ) )
        {
            return new DataOpClassification(
                IsDataOp: true,
                RequiresPreservation: true,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        if ( DmlVerb.IsMatch( statement ) )
        {
            return new DataOpClassification(
                IsDataOp: true,
                RequiresPreservation: true,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        if ( DdlVerb.IsMatch( statement ) )
        {
            return new DataOpClassification(
                IsDataOp: false,
                RequiresPreservation: false,
                IsUnclassified: false,
                RequiresAnnotation: emissionHint != null,
                EmissionHint: emissionHint );
        }

        // Unclassified: ADR-0019 default-deny posture. Caller must annotate
        // the migration with [DataMigration] or [StructuralOnly] before squash.
        return new DataOpClassification(
            IsDataOp: false,
            RequiresPreservation: false,
            IsUnclassified: true,
            RequiresAnnotation: true,
            EmissionHint: emissionHint );
    }

    /// <summary>
    /// Returns the set of non-deterministic function names detected in
    /// <paramref name="statement"/>. Empty when the statement is fully
    /// deterministic. Exposed so the squash codegen can surface
    /// non-determinism diagnostics independently of full classification
    /// (e.g., during the C12 determinism gate).
    /// </summary>
    public static string[] ScanNonDeterminism( string statement )
    {
        if ( string.IsNullOrEmpty( statement ) )
            return Array.Empty<string>();

        var hits = new SortedSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach ( Match m in NonDeterminism.Matches( statement ) )
            hits.Add( m.Groups[1].Value.ToLowerInvariant() );

        return hits.ToArray();
    }
}
