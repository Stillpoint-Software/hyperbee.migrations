using System;
using System.Linq;
using System.Threading.Tasks;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

public class CouchbaseRecordStore : IMigrationRecordStore
{
    private readonly IClusterProvider _clusterProvider;
    private readonly MigrationOptions _options;
    private readonly ILogger<CouchbaseRecordStore> _logger;

    public CouchbaseRecordStore( IClusterProvider clusterProvider, MigrationOptions options, ILogger<CouchbaseRecordStore> logger )
    {
        _clusterProvider = clusterProvider;
        _options = options;
        _logger = logger;
    }

    private async Task<ICouchbaseCollection> GetCollectionAsync()
    {
        var cluster = await _clusterProvider.GetClusterAsync();
        var bucket = await cluster.BucketAsync( _options.BucketName );
        var scope = await bucket.ScopeAsync( _options.ScopeName );
        var collection = await scope.CollectionAsync( _options.CollectionName );

        return collection;
    }

    public async Task<IDisposable> CreateMutexAsync()
    {
        // https://github.com/couchbaselabs/Couchbase.Extensions/blob/master/docs/locks.md

        var collection = await GetCollectionAsync();

        try
        {
            var mutex = await collection.RequestMutexAsync( _options.MutexName, _options.MutexExpireInterval );
            mutex.AutoRenew( _options.MutexRenewInterval, _options.MutexMaxLifetime );
            return mutex;
        }
        catch ( CouchbaseLockUnavailableException ex )
        {
            throw new MigrationMutexUnavailableException( $"The mutex `{_options.MutexName}` is unavailable.", ex );
        }
    }

    public async Task InitializeAsync()
    {
        var cluster = await _clusterProvider.GetClusterAsync();

        var bucketName = _options.BucketName;
        var scopeName = _options.ScopeName;
        var collectionName = _options.CollectionName;

        // check for bucket

        var hasBucketResult = await cluster.QueryAsync<int>( $"SELECT RAW count(*) FROM system:buckets WHERE name = '{bucketName}'" )
            .ConfigureAwait( false );

        if ( await hasBucketResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            throw new MigrationException( $"Missing bucket `{bucketName}`." );
        }

        // check for scope

        var hasScopeResult = await cluster.QueryAsync<int>( $"SELECT RAW count(*) FROM system:scopes WHERE `bucket` = '{bucketName}' AND name = '{scopeName}'" )
            .ConfigureAwait( false );

        if ( await hasScopeResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            _logger.LogInformation( "Creating scope `{bucketName}`.`{scopeName}`.", bucketName, scopeName );

            await cluster.QueryAsync<dynamic>( $"CREATE SCOPE `{bucketName}`.`{scopeName}`" )
                .ConfigureAwait( false );
        }

        // check for collection

        var hasCollectionResult = await cluster.QueryAsync<int>( $"SELECT RAW count(*) FROM system:keyspaces WHERE `bucket` = '{bucketName}' AND `scope` = '{scopeName}' AND name = '{collectionName}'" )
            .ConfigureAwait( false );

        if ( await hasCollectionResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            _logger.LogInformation( "Creating collection `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await cluster.QueryAsync<dynamic>( $"CREATE COLLECTION `{bucketName}`.`{scopeName}`.`{collectionName}`" );
        }

        // check for primary index

        var hasPrimaryIndexResult = await cluster.QueryAsync<int>( $"SELECT RAW count(*) FROM system:indexes WHERE bucket_id = '{bucketName}' AND scope_id = '{scopeName}' AND keyspace_id = '{collectionName}' AND is_primary" )
            .ConfigureAwait( false );

        if ( await hasPrimaryIndexResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            _logger.LogInformation( "Creating primary index `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await cluster.QueryAsync<dynamic>( $"CREATE PRIMARY INDEX ON `default`:`{bucketName}`.`{scopeName}`.`{collectionName}`" );
        }
    }

    public async Task<bool> ExistsAsync( string recordId )
    {
        var collection = await GetCollectionAsync();
        var check = await collection.ExistsAsync( recordId ).ConfigureAwait( false );

        return check.Exists;
    }

    public async Task DeleteAsync( string recordId )
    {
        var collection = await GetCollectionAsync();
        await collection.RemoveAsync( recordId ).ConfigureAwait( false );
    }

    public async Task StoreAsync( string recordId )
    {
        var collection = await GetCollectionAsync();

        var record = new MigrationRecord
        {
            Id = recordId
        };

        await collection.InsertAsync( recordId, record ).ConfigureAwait( false );
    }
}