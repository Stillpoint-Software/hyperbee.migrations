namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Aerospike statement kinds recognized by <see cref="AerospikeStatementClassifier"/>.
/// Mirrors <see cref="Parsers.AerospikeStatementType"/> with an additional
/// <see cref="Unknown"/> default-deny state for inputs the parser cannot consume.
/// </summary>
public enum AerospikeStatementKind : byte
{
    Unknown = 0,
    CreateIndex,
    DropIndex,
    CreateSet,
    Insert,
    Delete
}
