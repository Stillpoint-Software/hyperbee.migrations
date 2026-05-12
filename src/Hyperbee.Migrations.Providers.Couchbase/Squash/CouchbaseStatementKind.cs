namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Couchbase statement kinds surfaced by the squash classifier. Mirrors the
/// shape used by Aerospike + OpenSearch + MongoDB so the squash pipeline can
/// classify a statement irrespective of provider.
/// </summary>
/// <remarks>
/// <para>
/// Maps 1:1 from the existing <c>StatementType</c> emitted by
/// <see cref="Parsers.StatementParser"/> with the parser's partial grammar:
/// <c>CREATE BUCKET</c>, <c>CREATE [PRIMARY] INDEX</c>,
/// <c>CREATE SCOPE</c>, <c>CREATE COLLECTION</c>, <c>DROP BUCKET</c>,
/// <c>DROP SCOPE</c>, <c>DROP COLLECTION</c>, <c>UPDATE keyspace</c>,
/// <c>BUILD INDEX</c>. The parser is partial -- DML shapes (INSERT/UPSERT/
/// DELETE/MERGE) the parser does not consume fall through to
/// <see cref="Unknown"/>; the data-op classifier handles those by leading-
/// keyword recognition independently.
/// </para>
/// </remarks>
public enum CouchbaseStatementKind
{
    /// <summary>Unknown / unsupported statement -- parser rejected the input.</summary>
    Unknown,

    /// <summary>CREATE BUCKET keyspace [TYPE ...] [RAMQUOTA ...] ...</summary>
    CreateBucket,

    /// <summary>CREATE INDEX name ON keyspace(...)</summary>
    CreateIndex,

    /// <summary>CREATE PRIMARY INDEX [name] ON keyspace</summary>
    CreatePrimaryIndex,

    /// <summary>CREATE SCOPE keyspace</summary>
    CreateScope,

    /// <summary>CREATE COLLECTION keyspace</summary>
    CreateCollection,

    /// <summary>DROP BUCKET keyspace</summary>
    DropBucket,

    /// <summary>DROP SCOPE keyspace</summary>
    DropScope,

    /// <summary>DROP COLLECTION keyspace</summary>
    DropCollection,

    /// <summary>UPDATE keyspace SET ... -- a data op.</summary>
    Update,

    /// <summary>BUILD INDEX ON keyspace(...)</summary>
    BuildIndex
}
