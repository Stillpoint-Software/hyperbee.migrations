using System;
using System.Linq;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase
{
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

            await CreateAsync(
                cluster,
                () => _logger.LogInformation( "Creating scope `{bucketName}`.`{scopeName}`.", bucketName, scopeName ),
                testStatement: $"SELECT RAW count(*) FROM system:scopes WHERE `bucket` = '{bucketName}' AND name = '{scopeName}'",
                createStatement: $"CREATE SCOPE `{bucketName}`.`{scopeName}`",
                maxAttempts: 10,
                retryInterval: TimeSpan.FromSeconds( 3 )
            );

            // check for collection

            await CreateAsync(
                cluster,
                () => _logger.LogInformation( "Creating collection `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName ),
                testStatement: $"SELECT RAW count(*) FROM system:keyspaces WHERE `bucket` = '{bucketName}' AND `scope` = '{scopeName}' AND name = '{collectionName}'",
                createStatement: $"CREATE COLLECTION `{bucketName}`.`{scopeName}`.`{collectionName}`",
                maxAttempts: 10,
                retryInterval: TimeSpan.FromSeconds( 3 )
            );

            // check for primary index

            await CreateAsync(
                cluster,
                () => _logger.LogInformation( "Creating primary index `{bucketName}`.`{scopeName}`.`{collectionName}`.", bucketName, scopeName, collectionName ),
                testStatement: $"SELECT RAW count(*) FROM system:indexes WHERE bucket_id = '{bucketName}' AND scope_id = '{scopeName}' AND keyspace_id = '{collectionName}' AND is_primary",
                createStatement: $"CREATE PRIMARY INDEX ON `default`:`{bucketName}`.`{scopeName}`.`{collectionName}`",
                maxAttempts: 10,
                retryInterval: TimeSpan.FromSeconds( 3 )
            );
        }

        private async Task CreateAsync( ICluster cluster, Action logAction, string testStatement, string createStatement, int maxAttempts = 0, TimeSpan retryInterval = default )
        {
            var hasPrimaryIndexResult = await cluster.QueryAsync<int>( testStatement )
                .ConfigureAwait( false );

            if ( await hasPrimaryIndexResult.Rows.FirstOrDefaultAsync() == 0 )
            {
                logAction();

                await cluster.QueryAsync<dynamic>( createStatement )
                    .ConfigureAwait( false );

                // wait for creation
                //
                while ( maxAttempts-- > 0 )
                {
                    hasPrimaryIndexResult = await cluster.QueryAsync<int>( testStatement )
                        .ConfigureAwait( false );

                    if ( await hasPrimaryIndexResult.Rows.FirstOrDefaultAsync() > 0 )
                        break;

                    _logger.LogInformation( "WAITING..." );
                    await Task.Delay( retryInterval );
                }
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
}