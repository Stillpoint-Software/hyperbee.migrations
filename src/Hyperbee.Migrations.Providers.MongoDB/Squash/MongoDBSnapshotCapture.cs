using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// Production capture helper that turns a live <see cref="IMongoClient"/>
/// into the section-headered snapshot blob consumed by
/// <see cref="MongoDBSnapshotCanonicalizer"/> and
/// <see cref="IntrospectionSnapshotStrategy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Probes the cluster for structural state: <c>listCollections</c> for the
/// per-collection options (type, validator, time-series settings, capped
/// flags), then per-collection <c>indexes</c> for the index set. Result
/// is assembled as a UTF-8 string with section headers
/// (<c>[collections]</c>, <c>[indexes]</c>) keyed by collection name.
/// </para>
/// <para>
/// BSON-to-JSON serialization uses
/// <see cref="JsonOutputMode.CanonicalExtendedJson"/> so MongoDB-specific
/// types (ObjectId, BinData, Decimal128, BsonDateTime) round-trip
/// losslessly via the Extended JSON v2 spec. The canonicalizer downstream
/// treats the result as opaque JSON strings (BSON content rides through
/// per the OpenSearch precedent established in Task 2.0 painless spike).
/// </para>
/// <para>
/// System collections (<c>system.*</c>) are filtered out by default --
/// the ledger collection itself, MongoDB internal collections
/// (<c>system.indexes</c>, <c>system.namespaces</c>, etc.), and the
/// implicit <c>_id_</c> index are operational infrastructure, not
/// migration-managed state.
/// </para>
/// </remarks>
public static class MongoDBSnapshotCapture
{
    /// <summary>
    /// Captures the structural state of the supplied database as a section-
    /// headered snapshot blob suitable for
    /// <see cref="MongoDBSnapshotCanonicalizer.Canonicalize"/>.
    /// </summary>
    public static async Task<string> CaptureAsync(
        IMongoClient client,
        string databaseName,
        CancellationToken cancellationToken = default )
    {
        if ( client == null )
            throw new ArgumentNullException( nameof( client ) );
        if ( string.IsNullOrWhiteSpace( databaseName ) )
            throw new ArgumentException( "Database name is required.", nameof( databaseName ) );

        cancellationToken.ThrowIfCancellationRequested();

        var database = client.GetDatabase( databaseName );

        // listCollections per database. Returns one BsonDocument per
        // collection with shape:
        //   { name, type, options, info, idIndex }
        // Capture the full document; the canonicalizer strips ephemeral
        // sub-fields (uuid, readOnly, v, ns) at every nesting level.
        using var collectionsCursor = await database
            .ListCollectionsAsync( cancellationToken: cancellationToken )
            .ConfigureAwait( false );

        var collectionDocs = await collectionsCursor.ToListAsync( cancellationToken ).ConfigureAwait( false );

        var collections = new SortedDictionary<string, BsonDocument>( StringComparer.Ordinal );
        var indexes = new SortedDictionary<string, BsonArray>( StringComparer.Ordinal );

        foreach ( var doc in collectionDocs )
        {
            if ( !doc.TryGetValue( "name", out var nameValue ) || !nameValue.IsString )
                continue;

            var collectionName = nameValue.AsString;

            // Skip system collections -- operational infrastructure, not
            // migration-managed state. Filtering at the capture layer keeps
            // the canonical output meaningful regardless of which provider
            // collections happen to live in the operator's database.
            if ( collectionName.StartsWith( "system.", StringComparison.Ordinal ) )
                continue;

            // Strip the per-collection "name" field; the dictionary key
            // already holds it. The remaining document is the collection's
            // structural metadata.
            var collectionMetadata = doc.DeepClone().AsBsonDocument;
            collectionMetadata.Remove( "name" );
            collections[collectionName] = collectionMetadata;

            // Capture indexes for each collection. Filter the implicit
            // _id_ index -- it's automatic on every collection and adds
            // noise to the canonical diff. Operators can override by
            // recording their own custom _id_ index if needed.
            cancellationToken.ThrowIfCancellationRequested();
            var collection = database.GetCollection<BsonDocument>( collectionName );
            using var indexCursor = await collection.Indexes.ListAsync( cancellationToken )
                .ConfigureAwait( false );
            var indexDocs = await indexCursor.ToListAsync( cancellationToken ).ConfigureAwait( false );

            var indexArray = new BsonArray();
            foreach ( var indexDoc in indexDocs.OrderBy( d => d.TryGetValue( "name", out var n ) && n.IsString ? n.AsString : "", StringComparer.Ordinal ) )
            {
                if ( indexDoc.TryGetValue( "name", out var ixName ) && ixName.IsString
                     && string.Equals( ixName.AsString, "_id_", StringComparison.Ordinal ) )
                    continue;
                indexArray.Add( indexDoc );
            }
            if ( indexArray.Count > 0 )
                indexes[collectionName] = indexArray;
        }

        return ComposeBlob( databaseName, collections, indexes );
    }

    /// <summary>
    /// Assembles the section-headered snapshot blob from sorted collection
    /// + index dictionaries. Exposed for callers that already hold captured
    /// data (test fixtures, custom harnesses).
    /// </summary>
    public static string ComposeBlob(
        string databaseName,
        IReadOnlyDictionary<string, BsonDocument> collections,
        IReadOnlyDictionary<string, BsonArray> indexes )
    {
        if ( collections == null )
            throw new ArgumentNullException( nameof( collections ) );
        if ( indexes == null )
            throw new ArgumentNullException( nameof( indexes ) );

        var sb = new StringBuilder();
        sb.Append( "# mongodb-snapshot v1\n" );
        sb.Append( "# database: " ).Append( databaseName ?? "" ).Append( '\n' );
        sb.Append( '\n' );

        // [collections] section: BsonDocument with collection names as keys.
        if ( collections.Count > 0 )
        {
            sb.Append( "[collections]\n" );
            var collectionsDoc = new BsonDocument();
            foreach ( var (name, meta) in collections.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
                collectionsDoc[name] = meta;
            sb.Append( ToCanonicalJson( collectionsDoc ) ).Append( '\n' );
            sb.Append( '\n' );
        }

        // [indexes] section: BsonDocument keyed by collection name -> index array.
        if ( indexes.Count > 0 )
        {
            sb.Append( "[indexes]\n" );
            var indexesDoc = new BsonDocument();
            foreach ( var (name, indexArr) in indexes.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
                indexesDoc[name] = indexArr;
            sb.Append( ToCanonicalJson( indexesDoc ) ).Append( '\n' );
            sb.Append( '\n' );
        }

        return sb.ToString();
    }

    private static string ToCanonicalJson( BsonDocument document )
    {
        // CanonicalExtendedJson is the round-trippable form per Extended
        // JSON v2: ObjectId -> {"$oid":...}, Date -> {"$date":...}, etc.
        // The canonicalizer treats the result as opaque JSON content.
        return document.ToJson( new JsonWriterSettings
        {
            OutputMode = JsonOutputMode.CanonicalExtendedJson
        } );
    }
}
