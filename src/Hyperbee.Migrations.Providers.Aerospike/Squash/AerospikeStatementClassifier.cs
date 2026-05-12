using Hyperbee.Migrations.Providers.Aerospike.Parsers;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Per-statement classifier that delegates to <see cref="AerospikeStatementParser"/>
/// and lifts the parser's <see cref="AerospikeStatementItem"/> into a typed
/// <see cref="ClassifiedStatement"/> consumed by the snapshot strategy and
/// verifier.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the Postgres reference shape ([PostgresStatementClassifier.cs])
/// but is dramatically smaller (~50 LOC vs ~289 LOC) because Aerospike's
/// statement surface is comparatively narrow: only CREATE INDEX, DROP INDEX,
/// CREATE SET, INSERT INTO, DELETE FROM are recognized at v1. The parser
/// itself is the source of truth for grammar; this classifier is only
/// responsible for turning a single statement into a kind+name tuple and
/// graceful default-deny on unknown shapes.
/// </para>
/// <para>
/// UDF statements (CREATE/DROP UDF) are NOT currently part of the parser
/// grammar; if Phase 1 Task 1.5 (InfoSnapshotStrategy) decides to round-trip
/// UDFs, the parser grammar grows first and the <see cref="AerospikeStatementKind"/>
/// enum gains <c>CreateUdf</c>/<c>DropUdf</c> in lockstep.
/// </para>
/// </remarks>
public sealed record ClassifiedStatement(
    AerospikeStatementKind Kind,
    string Namespace,
    string SetName,
    string ObjectName,
    string Body,
    string Detail = null );

public static class AerospikeStatementClassifier
{
    /// <summary>
    /// Classifies a single Aerospike statement. Returns a record with
    /// <see cref="AerospikeStatementKind.Unknown"/> + <paramref name="statement"/>
    /// preserved as <see cref="ClassifiedStatement.Body"/> when the parser
    /// cannot consume the input.
    /// </summary>
    public static ClassifiedStatement Classify( string statement )
    {
        if ( string.IsNullOrWhiteSpace( statement ) )
            return new ClassifiedStatement( AerospikeStatementKind.Unknown, null, null, null, statement ?? "" );

        AerospikeStatementItem parsed;
        try
        {
            parsed = new AerospikeStatementParser().ParseStatement( statement );
        }
        catch ( NotSupportedException ex )
        {
            return new ClassifiedStatement(
                Kind: AerospikeStatementKind.Unknown,
                Namespace: null,
                SetName: null,
                ObjectName: null,
                Body: statement,
                Detail: ex.Message );
        }

        var kind = parsed.StatementType switch
        {
            AerospikeStatementType.CreateIndex => AerospikeStatementKind.CreateIndex,
            AerospikeStatementType.DropIndex => AerospikeStatementKind.DropIndex,
            AerospikeStatementType.CreateSet => AerospikeStatementKind.CreateSet,
            AerospikeStatementType.Insert => AerospikeStatementKind.Insert,
            AerospikeStatementType.Delete => AerospikeStatementKind.Delete,
            _ => AerospikeStatementKind.Unknown
        };

        return new ClassifiedStatement(
            Kind: kind,
            Namespace: parsed.Namespace,
            SetName: parsed.SetName,
            ObjectName: parsed.IndexName,
            Body: statement );
    }
}
