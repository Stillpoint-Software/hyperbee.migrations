using System;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Wait;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Couchbase;

internal class CouchbaseRecordStore : IMigrationRecordStore
{
    private readonly IClusterProvider _clusterProvider;
    private readonly CouchbaseMigrationOptions _options;
    private readonly ICouchbaseBootstrapper _bootstrapper;
    private readonly ICouchbaseRestApiService _restApiService;
    private readonly ILogger<CouchbaseRecordStore> _logger;

    public CouchbaseRecordStore( IClusterProvider clusterProvider, CouchbaseMigrationOptions options, ICouchbaseBootstrapper bootstrapper, ICouchbaseRestApiService restApiService, ILogger<CouchbaseRecordStore> logger )
    {
        _clusterProvider = clusterProvider;
        _options = options;
        _bootstrapper = bootstrapper;
        _restApiService = restApiService;
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
                _options.ClusterReadyTimeout,
                new PauseRetryStrategy(),
                cancellationToken
            );

            // we created the bucket, and it exists but couchbase my not have reported it yet.
            // wait for the bucket to be ready.
            //
            // bucket.WaitUntilReadyAsync() will return ready when the bucket is ready but the node is in warmup.
            // this will lead to exceptions on n1ql and other operations. we will use the rest api instead of
            // the client implementation.

            _logger.LogInformation( "Waiting for ledger bucket ready." );

            await _restApiService.WaitUntilBucketHealthyAsync( bucketName, _options.ClusterReadyTimeout, cancellationToken ).ConfigureAwait( false );
            await _restApiService.WaitUntilClusterHealthyAsync( _options.ClusterReadyTimeout, cancellationToken ).ConfigureAwait( false );

            // now it is safe to create the indexes
            _logger.LogInformation( "Creating ledger bucket indexes." );

            await cluster.QueryIndexes.CreatePrimaryIndexAsync( bucketName ).ConfigureAwait( false );
            await cluster.QueryIndexes.CreateIndexAsync( bucketName, "ix_type", new[] { "type" } ).ConfigureAwait( false );
        }

        // check for scope

        _logger.LogInformation( "Ensuring ledger scope `{bucketName}`.`{scopeName}` exists.", bucketName, scopeName );

        try
        {
            await clusterHelper.CreateScopeAsync( bucketName, scopeName ).ConfigureAwait( false );
            _logger.LogInformation( "Ledger scope created successfully." );
        }
        catch ( Exception ex ) when ( ex.Message.Contains( "already exists" ) || ex.Message.Contains( "scope already exists" ) )
        {
            _logger.LogInformation( "Ledger scope already exists." );
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "Failed to create ledger scope `{bucketName}`.`{scopeName}`.", bucketName, scopeName );
            
            // Don't fail for scope creation issues - try to continue
            _logger.LogWarning( "Continuing despite scope creation failure." );
        }

        // check for collection

        _logger.LogInformation( "Ensuring ledger collection `{bucketName}`.`{scopeName}`.`{collectionName}` exists.", bucketName, scopeName, collectionName );

        try
        {
            await clusterHelper.CreateCollectionAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false );
            _logger.LogInformation( "Ledger collection created successfully." );
        }
        catch ( Exception ex ) when ( ex.Message.Contains( "already exists" ) || ex.Message.Contains( "collection already exists" ) )
        {
            _logger.LogInformation( "Ledger collection already exists." );
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "Failed to create ledger collection `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );
            
            // Don't fail for collection creation issues - try to continue
            _logger.LogWarning( "Continuing despite collection creation failure." );
        }

        // wait for n1ql to `see` the collection and scope
        // there is a small window after the management commands create a scope or collection before n1ql sees them.

        try
        {
            _logger.LogInformation( "Waiting for N1QL visibility of ledger collection..." );
            
            await WaitHelper.WaitUntilAsync(
                async _ => await clusterHelper.CollectionExistsQueryAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false ),
                TimeSpan.FromSeconds( 30 ), // Shorter timeout for N1QL check
                new PauseRetryStrategy( TimeSpan.FromMilliseconds( 500 ) ), // Shorter retry intervals
                cancellationToken
            );
            
            _logger.LogInformation( "Ledger collection is visible to N1QL." );
        }
        catch ( Exception ex )
        {
            _logger.LogWarning( ex, "N1QL visibility check failed for ledger collection `{bucketName}`.`{scopeName}`.`{collectionName}`. Proceeding anyway.", bucketName, scopeName, collectionName );
            // Don't throw - proceed with index creation anyway
        }

        // check for primary index

        _logger.LogInformation( "Ensuring ledger primary index `{bucketName}`.`{scopeName}`.`{collectionName}` exists.", bucketName, scopeName, collectionName );

        try
        {
            await clusterHelper.CreatePrimaryCollectionIndexAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false );
            _logger.LogInformation( "Ledger primary index created successfully." );
        }
        catch ( Exception ex ) when ( ex.Message.Contains( "already exists" ) || ex.Message.Contains( "index already exists" ) )
        {
            _logger.LogInformation( "Ledger primary index already exists." );
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "Failed to create ledger primary index `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName );
            
            // Don't fail for index creation issues - try to continue
            _logger.LogWarning( "Continuing despite primary index creation failure." );
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

    public async Task WriteAsync( string recordId )
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
