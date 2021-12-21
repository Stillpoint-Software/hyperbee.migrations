using System;
using System.Threading.Tasks;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
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

    private async Task WaitForStoreAsync()
    {
        _logger.LogInformation( "Waiting for cluster..." );

        var cluster = await _clusterProvider.GetClusterAsync();
        await cluster.WaitUntilReadyAsync( _options.ClusterReadyTimeout );
    }

    public async Task InitializeAsync()
    {
        // wait for cluster ready

        await WaitForStoreAsync();

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync();
        var cluster = clusterHelper.Cluster;

        var (bucketName, scopeName, collectionName) = _options;
        var waitSettings = new WaitSettings( _options.ProvisionRetryInterval, _options.ProvisionAttempts );

        // check for bucket

        if ( !await clusterHelper.BucketExistsAsync( bucketName ) )
        {
            _logger.LogInformation( "Creating ledger bucket `{name}`.", bucketName );

            await cluster.Buckets.CreateBucketAsync( new BucketSettings
                {
                    Name = bucketName,
                    RamQuotaMB = 100,
                    FlushEnabled = true
                } )
                .ConfigureAwait( false );

            await clusterHelper.WaitUntilAsync(
                async () => await clusterHelper.BucketExistsAsync( bucketName ),
                waitSettings,
                _logger
            );

            _logger.LogInformation( "Creating ledger bucket indexes." );

            await cluster.QueryIndexes.CreatePrimaryIndexAsync( bucketName );
            await cluster.QueryIndexes.CreateIndexAsync( bucketName, "ix_type", new [] { "type" } );
        }

        var bucket = await cluster.BucketAsync( bucketName );
        await bucket.WaitUntilReadyAsync( _options.ClusterReadyTimeout );

        // check for scope

        if ( !await clusterHelper.ScopeExistsAsync( bucketName, scopeName ) )
        {
            _logger.LogInformation( "Creating ledger scope `{bucketName}`.`{scopeName}`.", bucketName, scopeName );
            await clusterHelper.CreateScopeAsync( bucketName, scopeName );

            await clusterHelper.WaitUntilAsync(
                async () => await clusterHelper.ScopeExistsAsync( bucketName, scopeName ),
                waitSettings,
                _logger
            );
        }

        // check for collection

        if ( !await clusterHelper.CollectionExistsAsync( bucketName, scopeName, collectionName ) )
        {
            _logger.LogInformation( "Creating ledger collection `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await clusterHelper.CreateCollectionAsync( bucketName, scopeName, collectionName );

            await clusterHelper.WaitUntilAsync(
                async () => await clusterHelper.CollectionExistsAsync( bucketName, scopeName, collectionName ),
                waitSettings,
                _logger
            );
        }

        // check for primary index

        if ( !await clusterHelper.PrimaryCollectionIndexExistsAsync( bucketName, scopeName, collectionName ) )
        {
            _logger.LogInformation( "Creating ledger primary index `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await clusterHelper.CreatePrimaryCollectionIndexAsync( bucketName, scopeName, collectionName );

            await clusterHelper.WaitUntilAsync(
                async () => await clusterHelper.CollectionExistsAsync( bucketName, scopeName, collectionName ),
                waitSettings,
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