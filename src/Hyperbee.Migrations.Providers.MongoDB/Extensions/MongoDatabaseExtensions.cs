using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Extensions;

public static class MongoDatabaseExtensions
{
    public static async Task<bool> CollectionExistsAsync( this IMongoDatabase database, string collectionName )
    {
        var options = new ListCollectionsOptions { Filter = new BsonDocument( "name", collectionName ) };

        var collections = await database.ListCollectionsAsync( options );
        return await collections.AnyAsync();
    }

    public static async Task RenameCollectionPropertyAsync( this IMongoDatabase database, string collectionName, string oldName, string newName )
    {
        var collection = database.GetCollection<BsonDocument>( collectionName );

        await collection
            .Find( Builders<BsonDocument>.Filter.Empty )
            .ForEachAsync( async document =>
            {
                if ( document.Contains( oldName ) )
                {
                    document.RenameProperty( oldName, newName );
                    await collection
                        .ReplaceOneAsync(
                            Builders<BsonDocument>.Filter.Eq( "_id", document.GetValue( "_id" ) ), document );
                }
            } );
    }

    public static async Task RemoveCollectionPropertyAsync( this IMongoDatabase database, string collectionName, string name )
    {
        var collection = database.GetCollection<BsonDocument>( collectionName );

        await collection
            .Find( Builders<BsonDocument>.Filter.Empty )
            .ForEachAsync( async document =>
            {
                if ( document.Contains( name ) )
                {
                    document.RemoveProperty( name );
                    await collection
                        .ReplaceOneAsync(
                            Builders<BsonDocument>.Filter.Eq( "_id", document.GetValue( "_id" ) ), document );
                }
            } );
    }


    public static async Task AddCollectionIndexAsync( this IMongoDatabase database, string collectionName, string fieldName )
    {
        var collection = database.GetCollection<BsonDocument>( collectionName );
        var indexBuilder = Builders<BsonDocument>.IndexKeys;
        var keys = indexBuilder.Ascending( fieldName );
        var options = new CreateIndexOptions { Name = fieldName };
        var indexModel = new CreateIndexModel<BsonDocument>( keys, options );
        await collection.Indexes.CreateOneAsync( indexModel, cancellationToken: default );
    }
}
