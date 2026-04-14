using Aerospike.Client;
using Hyperbee.Migrations.Wait;

namespace Hyperbee.Migrations.Providers.Aerospike.Extensions;

public enum IndexCreateMode
{
    // No-op if the index already exists. Idempotent create.
    Missing,

    // Drop the existing index (if any), then create. Always ends in a fresh index.
    Recreate
}

public static class AerospikeClientExtensions
{
    // indexes

    public static Task<bool> IndexExistsAsync( this IAerospikeClient client, string ns, string indexName )
    {
        var node = client.Nodes.FirstOrDefault();

        if ( node == null )
            return Task.FromResult( false );

        string response;

        try
        {
            response = Info.Request( node, "sindex/" + ns );
        }
        catch
        {
            return Task.FromResult( false );
        }

        return Task.FromResult( ContainsIndex( response, indexName ) );
    }

    // Aerospike `sindex/<ns>` info response format:
    //   `ns=X:indexname=Y:set=Z:bin=W:type=...;ns=X:indexname=Y2:...`
    //
    // Match `indexname=<name>:` (with trailing colon) to avoid prefix false-positives
    // (e.g. `idx_foo` matching `idx_foo_bar`).

    internal static bool ContainsIndex( string response, string indexName )
    {
        if ( string.IsNullOrEmpty( response ) )
            return false;

        return response.Contains( $"indexname={indexName}:", StringComparison.OrdinalIgnoreCase );
    }

    public static async Task CreateIndexAsync(
        this IAerospikeClient client,
        string ns,
        string setName,
        string indexName,
        string binName,
        IndexType indexType,
        IndexCreateMode mode = IndexCreateMode.Missing,
        bool waitReady = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default )
    {
        var exists = await client.IndexExistsAsync( ns, indexName ).ConfigureAwait( false );

        switch ( mode )
        {
            case IndexCreateMode.Missing when exists:
                return;

            case IndexCreateMode.Recreate when exists:
                await client.DropIndexAsync( ns, setName, indexName ).ConfigureAwait( false );
                break;
        }

        try
        {
            client.CreateIndex( null, ns, setName, indexName, binName, indexType );
        }
        catch ( AerospikeException ex ) when ( ex.Result == ResultCode.INDEX_ALREADY_EXISTS )
        {
            // race condition: another process created the same index concurrently
        }

        if ( waitReady )
            await client.WaitForIndexReadyAsync( ns, indexName, timeout, cancellationToken ).ConfigureAwait( false );
    }

    public static Task DropIndexAsync( this IAerospikeClient client, string ns, string setName, string indexName )
    {
        try
        {
            client.DropIndex( null, ns, setName, indexName );
        }
        catch ( AerospikeException ex ) when ( ex.Result == ResultCode.INDEX_NOTFOUND )
        {
            // already dropped
        }

        return Task.CompletedTask;
    }

    public static async Task WaitForIndexReadyAsync(
        this IAerospikeClient client,
        string ns,
        string indexName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default )
    {
        await WaitHelper.WaitUntilAsync(
            _ =>
            {
                var node = client.Nodes.FirstOrDefault();

                if ( node == null )
                    return Task.FromResult( false );

                try
                {
                    var response = Info.Request( node, $"sindex/{ns}/{indexName}" );
                    return Task.FromResult( !string.IsNullOrEmpty( response ) && !response.Contains( "FAIL" ) );
                }
                catch
                {
                    return Task.FromResult( false );
                }
            },
            timeout ?? TimeSpan.FromSeconds( 60 ),
            new BackoffRetryStrategy( TimeSpan.FromMilliseconds( 500 ), TimeSpan.FromSeconds( 5 ) ),
            cancellationToken
        ).ConfigureAwait( false );
    }

    // records

    public static Task UpsertAsync(
        this IAsyncClient client,
        string ns,
        string setName,
        string id,
        IReadOnlyDictionary<string, object> bins,
        CancellationToken cancellationToken = default )
    {
        var key = new Key( ns, setName, id );
        var binArray = bins.Select( kvp => new Bin( kvp.Key, kvp.Value ) ).ToArray();

        return client.Put( null, cancellationToken, key, binArray );
    }

    public static Task UpsertAsync(
        this IAsyncClient client,
        string ns,
        string setName,
        string id,
        Bin[] bins,
        CancellationToken cancellationToken = default )
    {
        var key = new Key( ns, setName, id );
        return client.Put( null, cancellationToken, key, bins );
    }
}
