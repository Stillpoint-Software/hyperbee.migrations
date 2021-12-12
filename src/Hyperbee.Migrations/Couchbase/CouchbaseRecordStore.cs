using System;
using System.Threading.Tasks;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

public class CouchbaseRecordStore : IMigrationRecordStore
{
    private readonly IClusterProvider _clusterProvider;
    private readonly CouchbaseMigrationOptions _options;
    private readonly ILogger<CouchbaseRecordStore> _logger;

    public CouchbaseRecordStore( IClusterProvider clusterProvider, CouchbaseMigrationOptions options, ILogger<CouchbaseRecordStore> logger )
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

    public async Task InitializeAsync()
    {
        // wait for cluster ready

        var cluster = await _clusterProvider.GetClusterAsync();
        await cluster.WaitUntilReadyAsync( _options.ClusterReadyTimeout );

        var (bucketName, scopeName, collectionName) = _options;

        // check for bucket

        if ( !await CouchbaseHelper.BucketExistsAsync( _clusterProvider, bucketName ) )
            throw new MigrationException( $"Missing bucket `{bucketName}`." );

        // check for scope

        if ( !await CouchbaseHelper.ScopeExistsAsync( _clusterProvider, bucketName, scopeName ) )
        {
            _logger.LogInformation( "Creating scope `{bucketName}`.`{scopeName}`.", bucketName, scopeName );
            await CouchbaseHelper.CreateScopeAsync( _clusterProvider, bucketName, scopeName );

            await CouchbaseHelper.WaitUntilAsync(
                async () => await CouchbaseHelper.ScopeExistsAsync( _clusterProvider, bucketName, scopeName ),
                _options.ProvisionRetryInterval, 
                _options.ProvisionAttempts,
                _logger
            );
        }

        // check for collection

        if ( !await CouchbaseHelper.CollectionExistsAsync( _clusterProvider, bucketName, scopeName, collectionName ) )
        {
            _logger.LogInformation( "Creating collection `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await CouchbaseHelper.CreateCollectionAsync( _clusterProvider, bucketName, scopeName, collectionName );

            await CouchbaseHelper.WaitUntilAsync(
                async () => await CouchbaseHelper.CollectionExistsAsync( _clusterProvider, bucketName, scopeName, collectionName ),
                _options.ProvisionRetryInterval,
                _options.ProvisionAttempts,
                _logger
            );
        }

        // check for primary index

        if ( !await CouchbaseHelper.PrimaryCollectionIndexExistsAsync( _clusterProvider, bucketName, scopeName, collectionName ) )
        {
            _logger.LogInformation( "Creating primary index `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await CouchbaseHelper.CreatePrimaryCollectionIndexAsync( _clusterProvider, bucketName, scopeName, collectionName );

            await CouchbaseHelper.WaitUntilAsync(
                async () => await CouchbaseHelper.CollectionExistsAsync( _clusterProvider, bucketName, scopeName, collectionName ),
                _options.ProvisionRetryInterval,
                _options.ProvisionAttempts,
                _logger
            );
        }
    }

    public async Task<IDisposable> CreateLockAsync()
    {
        // https://github.com/couchbaselabs/Couchbase.Extensions/blob/master/docs/locks.md

        var collection = await GetCollectionAsync();

        try
        {
            var mutex = await collection.RequestMutexAsync( _options.LockName, _options.LockExpireInterval )
                .ConfigureAwait( false );

            mutex.AutoRenew( _options.LockRenewInterval, _options.LockMaxLifetime );
            return mutex;
        }
        catch ( CouchbaseLockUnavailableException ex )
        {
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ex );
        }
    }

    public async Task<bool> ExistsAsync( string recordId )
    {
        var collection = await GetCollectionAsync();

        var check = await collection.ExistsAsync( recordId )
            .ConfigureAwait( false );

        return check.Exists;
    }

    public async Task DeleteAsync( string recordId )
    {
        var collection = await GetCollectionAsync();

        await collection.RemoveAsync( recordId )
            .ConfigureAwait( false );
    }

    public async Task StoreAsync( string recordId )
    {
        var collection = await GetCollectionAsync();

        var record = new MigrationRecord
        {
            Id = recordId
        };

        await collection.InsertAsync( recordId, record )
            .ConfigureAwait( false );
    }
}