using System;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Hyperbee.Migrations.Providers.Couchbase.Wait;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Couchbase;

internal class CouchbaseRecordStore : IMigrationRecordStore
{
    private readonly IClusterProvider _clusterProvider;
    private readonly CouchbaseMigrationOptions _options;
    private readonly ICouchbaseBootstrapper _bootstrapper;
    private readonly ILogger<CouchbaseRecordStore> _logger;

    public CouchbaseRecordStore( IClusterProvider clusterProvider, CouchbaseMigrationOptions options, ICouchbaseBootstrapper bootstrapper, ILogger<CouchbaseRecordStore> logger )
    {
        _clusterProvider = clusterProvider;
        _options = options;
        _bootstrapper = bootstrapper;
        _logger = logger;
    }

    private async Task<ICouchbaseCollection> GetCollectionAsync()
    {
        var cluster = await _clusterProvider.GetClusterAsync().ConfigureAwait( false );
        var bucket = await cluster.BucketAsync( _options.BucketName ).ConfigureAwait( false );
        var scope = await bucket.ScopeAsync( _options.ScopeName ).ConfigureAwait( false );
        var collection = await scope.CollectionAsync( _options.CollectionName ).ConfigureAwait( false );

        return collection;
    }

    public async Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        // wait for system ready

        await _bootstrapper.WaitForSystemReadyAsync( _options.ClusterReadyTimeout, cancellationToken )
            .ConfigureAwait( false );

        // get the cluster

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync()
            .ConfigureAwait( false );

        var cluster = clusterHelper.Cluster;

        var (bucketName, scopeName, collectionName) = _options;

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

            await WaitHelper.WaitUntilAsync(
                async _ => await clusterHelper.BucketExistsAsync( bucketName ).ConfigureAwait( false ),
                TimeSpan.Zero,
                new PauseRetryStrategy(),
                cancellationToken
            );

            // we created the bucket and it exists but couchbase my not have reported it yet.
            // wait for the bucket to be ready.

            await Task.Delay( 1000, cancellationToken ).ConfigureAwait( false );
            var bucket = await cluster.BucketAsync( bucketName ).ConfigureAwait( false );
            await bucket.WaitUntilReadyAsync( _options.ClusterReadyTimeout ).ConfigureAwait( false );

            // now it is safe to create the indexes
            _logger.LogInformation( "Creating ledger bucket indexes." );

            await cluster.QueryIndexes.CreatePrimaryIndexAsync( bucketName ).ConfigureAwait( false );
            await cluster.QueryIndexes.CreateIndexAsync( bucketName, "ix_type", new [] { "type" } ).ConfigureAwait( false );
        }

        // check for scope

        if ( !await clusterHelper.ScopeExistsAsync( bucketName, scopeName ) )
        {
            _logger.LogInformation( "Creating ledger scope `{bucketName}`.`{scopeName}`.", bucketName, scopeName );
            await clusterHelper.CreateScopeAsync( bucketName, scopeName ).ConfigureAwait( false );

            await WaitHelper.WaitUntilAsync( 
                async _ => await clusterHelper.ScopeExistsAsync( bucketName, scopeName ).ConfigureAwait( false ), 
                TimeSpan.Zero, 
                new PauseRetryStrategy(), 
                cancellationToken 
            );
        }

        // check for collection

        if ( !await clusterHelper.CollectionExistsAsync( bucketName, scopeName, collectionName ) )
        {
            _logger.LogInformation( "Creating ledger collection `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await clusterHelper.CreateCollectionAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false );

            await WaitHelper.WaitUntilAsync(
                async _ => await clusterHelper.CollectionExistsAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false ),
                TimeSpan.Zero,
                new PauseRetryStrategy(),
                cancellationToken
            );
        }

        // wait for n1ql to `see` the collection and scope
        // there is a small window after the management commands create a scope or collection before n1ql sees them.

        await WaitHelper.WaitUntilAsync(
            async _ => await clusterHelper.CollectionExistsQueryAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false ),
            TimeSpan.Zero,
            new PauseRetryStrategy(),
            cancellationToken
        );

        // check for primary index

        if ( !await clusterHelper.PrimaryCollectionIndexExistsAsync( bucketName, scopeName, collectionName ) )
        {
            _logger.LogInformation( "Creating ledger primary index `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );

            await clusterHelper.CreatePrimaryCollectionIndexAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false );

            await WaitHelper.WaitUntilAsync(
                async _ => await clusterHelper.PrimaryCollectionIndexExistsAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false ),
                TimeSpan.Zero,
                new PauseRetryStrategy(),
                cancellationToken
            );
        }

        // ready

        _logger.LogInformation( "Ledger `{bucketName}` is ready.", bucketName );
    }

    public async Task<IDisposable> CreateLockAsync()
    {
        // https://github.com/couchbaselabs/Couchbase.Extensions/blob/master/docs/locks.md

        var collection = await GetCollectionAsync()
            .ConfigureAwait( false );

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
        var collection = await GetCollectionAsync()
            .ConfigureAwait( false );

        var check = await collection.ExistsAsync( recordId )
            .ConfigureAwait( false );

        return check.Exists;
    }

    public async Task DeleteAsync( string recordId )
    {
        var collection = await GetCollectionAsync()
            .ConfigureAwait( false );

        await collection.RemoveAsync( recordId )
            .ConfigureAwait( false );
    }

    public async Task StoreAsync( string recordId )
    {
        var collection = await GetCollectionAsync()
            .ConfigureAwait( false );

        var record = new MigrationRecord
        {
            Id = recordId
        };

        await collection.InsertAsync( recordId, record )
            .ConfigureAwait( false );
    }
}