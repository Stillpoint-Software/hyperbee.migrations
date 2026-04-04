using Aerospike.Client;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Aerospike.Samples.Migrations;

[Migration( 3000 )]
public class CodeMigration : Migration
{
    private readonly IAsyncClient _asyncClient;
    private readonly IAerospikeClient _client;
    private readonly ILogger<CodeMigration> _logger;

    public CodeMigration( IAsyncClient asyncClient, IAerospikeClient client, ILogger<CodeMigration> logger )
    {
        _asyncClient = asyncClient;
        _client = client;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // code migration: use the injected Aerospike clients to run operations directly

        _logger.LogInformation( "Running Aerospike code migration" );

        // create a secondary index using the sync client (index operations are synchronous)
        try
        {
            _client.CreateIndex(
                null,
                "test",
                "users",
                "idx_name_code",
                "name",
                IndexType.STRING
            );

            _logger.LogInformation( "Created index idx_name_code" );
        }
        catch ( AerospikeException ex ) when ( ex.Result == ResultCode.INDEX_ALREADY_EXISTS )
        {
            _logger.LogInformation( "Index idx_name_code already exists, skipping" );
        }

        // insert a record using the async client
        var key = new Key( "test", "users", "user-003" );

        await _asyncClient.Put(
            null,
            CancellationToken.None,
            key,
            new Bin( "name", "Bob Johnson" ),
            new Bin( "email", "bob@example.com" ),
            new Bin( "active", 1 ),
            new Bin( "role", "user" ),
            new Bin( "createdTimestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds() )
        ).ConfigureAwait( false );

        _logger.LogInformation( "Inserted user-003 via code migration" );

        _logger.LogInformation( "Aerospike code migration completed" );
    }
}
