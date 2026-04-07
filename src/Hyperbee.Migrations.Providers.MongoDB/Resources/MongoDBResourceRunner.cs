using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.MongoDB.Parsers;
using Hyperbee.Migrations.Resources;
using Hyperbee.Migrations.Wait;
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

    // statements

    public Task StatementsFromAsync( string resourceName, CancellationToken cancellationToken = default )
    {
        return StatementsFromAsync( new[] { resourceName }, default, cancellationToken );
    }

    public Task StatementsFromAsync( string resourceName, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        return StatementsFromAsync( new[] { resourceName }, timeout, cancellationToken );
    }

    public Task StatementsFromAsync( string[] resourceNames, CancellationToken cancellationToken = default )
    {
        return StatementsFromAsync( resourceNames, default, cancellationToken );
    }

    public async Task StatementsFromAsync( string[] resourceNames, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        static IEnumerable<MongoStatementItem> ReadResources( string migrationName, params string[] resourceNames )
        {
            foreach ( var resourceName in resourceNames )
            {
                var json = ResourceHelper.GetResource<TMigration>( $"{migrationName}.{resourceName}" );
                var node = JsonNode.Parse( json );

                var statements = node!["statements"]!
                    .AsArray()
                    .Select( x => x["statement"]?.ToString() )
                    .Where( x => x != null );

                var parser = new MongoStatementParser();

                foreach ( var statement in statements )
                    yield return parser.ParseStatement( statement );
            }
        }

        ThrowIfNoResourceLocationFor();

        var migrationName = Migration.VersionedName<TMigration>();

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        foreach ( var statementItem in ReadResources( migrationName, resourceNames ) )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            switch ( statementItem.StatementType )
            {
                case MongoStatementType.CreateCollection:
                    await CreateCollectionAsync( statementItem ).ConfigureAwait( false );
                    break;

                case MongoStatementType.DropCollection:
                    await DropCollectionAsync( statementItem ).ConfigureAwait( false );
                    break;

                case MongoStatementType.CreateIndex:
                    await CreateIndexAsync( statementItem, unique: false ).ConfigureAwait( false );
                    break;

                case MongoStatementType.CreateUniqueIndex:
                    await CreateIndexAsync( statementItem, unique: true ).ConfigureAwait( false );
                    break;

                case MongoStatementType.DropIndex:
                    await DropIndexAsync( statementItem ).ConfigureAwait( false );
                    break;

                case MongoStatementType.Insert:
                    _logger?.LogInformation( "INSERT INTO {database}.{collection} (use DocumentsFromAsync for document insertion)", statementItem.DatabaseName, statementItem.CollectionName );
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    // documents

    public async Task DocumentsFromAsync( string[] resourcePaths, CancellationToken cancellationToken = default )
    {
        var migrationName = Migration.VersionedName<TMigration>();

        foreach ( var item in ReadResources() )
        {
            try
            {
                var db = _client.GetDatabase( item.Location.Database );

                var collection = db.GetCollection<BsonDocument>( item.Location.Collection );
                var document = BsonDocument.Parse( item.Content );
                await collection.InsertOneAsync( document, options: new InsertOneOptions { BypassDocumentValidation = false }, cancellationToken ).ConfigureAwait( false );
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
            // validate resource path and split into database and collection parts

            var resourceParts = resourcePath.Split( '/' );
            var count = resourceParts.Length;

            if ( count != 2 )
                throw new ArgumentException( "Invalid resource path. Path must be in the form 'database/collection'.", nameof( resourcePaths ) );

            var databaseName = resourceParts[0];
            var collectionName = resourceParts[1];

            var location = new Location( databaseName, collectionName );

            return new DocumentItem( location, document );
        }

        IEnumerable<DocumentItem> ReadResources()
        {
            foreach ( var resourceName in resourcePaths )
            {
                var resource = $"{migrationName}.{resourceName}";
                _logger.LogInformation( " - Resource: [{resource}]", resource );

                var document = ResourceHelper.GetResource<TMigration>( resource );
                yield return CreateDocumentItem( resourceName, document );
            }
        }
    }

    // operations

    private async Task CreateCollectionAsync( MongoStatementItem item )
    {
        _logger?.LogInformation( "CREATE COLLECTION {database}.{collection}", item.DatabaseName, item.CollectionName );

        var db = _client.GetDatabase( item.DatabaseName );
        await db.CreateCollectionAsync( item.CollectionName ).ConfigureAwait( false );
    }

    private async Task DropCollectionAsync( MongoStatementItem item )
    {
        _logger?.LogInformation( "DROP COLLECTION {database}.{collection}", item.DatabaseName, item.CollectionName );

        var db = _client.GetDatabase( item.DatabaseName );
        await db.DropCollectionAsync( item.CollectionName ).ConfigureAwait( false );
    }

    private async Task CreateIndexAsync( MongoStatementItem item, bool unique )
    {
        var kind = unique ? "UNIQUE INDEX" : "INDEX";
        _logger?.LogInformation( "CREATE {kind} {indexName} ON {database}.{collection}", kind, item.IndexName, item.DatabaseName, item.CollectionName );

        var db = _client.GetDatabase( item.DatabaseName );
        var collection = db.GetCollection<BsonDocument>( item.CollectionName );

        var keysBuilder = Builders<BsonDocument>.IndexKeys;
        IndexKeysDefinition<BsonDocument> keys = null;

        foreach ( var field in item.FieldNames )
        {
            keys = keys == null
                ? keysBuilder.Ascending( field )
                : keys.Ascending( field );
        }

        var options = new CreateIndexOptions
        {
            Name = item.IndexName,
            Unique = unique
        };

        var model = new CreateIndexModel<BsonDocument>( keys, options );
        await collection.Indexes.CreateOneAsync( model ).ConfigureAwait( false );
    }

    private async Task DropIndexAsync( MongoStatementItem item )
    {
        _logger?.LogInformation( "DROP INDEX {indexName} ON {database}.{collection}", item.IndexName, item.DatabaseName, item.CollectionName );

        var db = _client.GetDatabase( item.DatabaseName );
        var collection = db.GetCollection<BsonDocument>( item.CollectionName );
        await collection.Indexes.DropOneAsync( item.IndexName ).ConfigureAwait( false );
    }

    // helpers

    private static void ThrowIfNoResourceLocationFor()
    {
        var exists = typeof( TMigration )
            .Assembly
            .GetCustomAttributes( typeof( ResourceLocationAttribute ), false )
            .Cast<ResourceLocationAttribute>()
            .Any();

        if ( !exists )
            throw new NotSupportedException( $"Missing required assembly attribute: {nameof( ResourceLocationAttribute )}." );
    }

    private record Location( string Database, string Collection );

    private record DocumentItem( Location Location, string Content );
}
