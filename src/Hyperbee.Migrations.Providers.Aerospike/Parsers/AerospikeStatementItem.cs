namespace Hyperbee.Migrations.Providers.Aerospike.Parsers;

public record AerospikeStatementItem(
    AerospikeStatementType StatementType,
    string Statement,
    string Namespace,
    string SetName,
    string IndexName = null,
    string BinName = null,
    AerospikeIndexType IndexType = AerospikeIndexType.Default,
    string Expression = null
)
{
    public bool Recreate { get; init; }
    public bool WaitReady { get; init; }
}
