using Hyperbee.Migrations.Resources;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Resources;

public class MongoDBResourceRunner<TMigration>
    where TMigration : Migration
{
    private readonly IMongoClient _client;
    private readonly ILogger _logger;

    public MongoDBResourceRunner(
        IMongoClient client,
        ILogger<TMigration> logger )
    {
        _client = client;
        _logger = logger;
    }

    // documents

    public async Task DocumentsFromAsync( string[] resourcePaths, CancellationToken cancellationToken = default )
    {
        var migrationName = Migration.VersionedName<TMigration>();

        foreach ( var item in ReadResources( migrationName, resourcePaths ) )
        {
            try
            {
                var db = _client.GetDatabase( item.Location.Database );

                var collection = db.GetCollection<BsonDocument>( item.Location.Collection );
                var document = BsonDocument.Parse( item.Content );
                await collection.InsertOneAsync( document, options: new InsertOneOptions { BypassDocumentValidation = false }, cancellationToken );
            }
            catch ( Exception ex )
            {
                _logger.LogError( ex, "Error executing document: `{json}`", item.Content );
                throw;
            }
        }

        return;

        static DocumentItem CreateDocumentItem( string resourcePath, string document )
        {
            // validate resource path and split into bucket, scope and collection parts

            var resourceParts = resourcePath.Split( '/' );
            var count = resourceParts.Length;

            if ( count == 2 )
                throw new ArgumentException( "Invalid resource path. Path must be in the form 'database/collection'.", nameof( resourcePaths ) );

            var databaseName = resourceParts[0];
            var collectionName = resourceParts[1];

            var location = new Location( databaseName, collectionName );

            return new DocumentItem( location, document );
        }

        static IEnumerable<DocumentItem> ReadResources( string migrationName, params string[] resourcePaths )
        {
            foreach ( var resourceName in resourcePaths )
            {
                var document = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceName}" );
                yield return CreateDocumentItem( resourceName, document );
            }
        }
    }

    private record Location( string Database, string Collection );

    private record DocumentItem( Location Location, string Content );

}
