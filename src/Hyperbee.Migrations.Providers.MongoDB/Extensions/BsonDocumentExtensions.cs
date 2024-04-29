using MongoDB.Bson;

namespace Hyperbee.Migrations.Providers.MongoDB.Extensions;

public static class BsonDocumentExtensions
{
    public static void RenameProperty( this BsonDocument document, string oldName, string newName )
    {
        var old = document.GetElement( oldName );
        var newElement = new BsonElement( newName, BsonValue.Create( old.Value ) );

        document.Remove( oldName );
        document.Add( newElement );
    }

    public static void RemoveProperty( this BsonDocument document, string property )
    {
        document.Remove( property );
    }

    public static void AddProperty( this BsonDocument document, string property, object value )
    {
        var element = new BsonElement( property, BsonValue.Create( value ) );
        document.Add( element );
    }

    public static void AddUniqueIdentifier( this BsonDocument document, Guid value )
    {
        document.AddProperty( "_id", value );
    }

    public static void ChangeValue( this BsonDocument document, string property, object value )
    {
        var newElement = new BsonElement( property, BsonValue.Create( value ) );

        document.Remove( property );
        document.Add( newElement );
    }
}
