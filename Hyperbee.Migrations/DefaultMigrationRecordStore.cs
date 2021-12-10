using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Couchbase.Query;

namespace Hyperbee.Migrations;

public class DefaultMigrationRecordStore : IMigrationRecordStore
{
    private readonly IClusterProvider _clusterProvider;
    private readonly IBucketProvider _bucketProvider;
    private readonly MigrationOptions _options;

    public DefaultMigrationRecordStore( IClusterProvider clusterProvider, IBucketProvider bucketProvider, MigrationOptions options )
    {
        _clusterProvider = clusterProvider;
        _bucketProvider = bucketProvider;
        _options = options;
    }

    private async Task<ICouchbaseCollection> GetCollectionAsync()
    {
        var bucket = await _bucketProvider.GetBucketAsync( _options.BucketName );
        var scope = await bucket.ScopeAsync( _options.ScopeName );
        var collection = await scope.CollectionAsync( _options.CollectionName );
        
        return collection;
    }

    public async Task<IDisposable> CreateMutexAsync()
    {
        // https://github.com/couchbaselabs/Couchbase.Extensions/blob/master/docs/locks.md

        var bucket = await _bucketProvider.GetBucketAsync( _options.BucketName );
        var scope = await bucket.ScopeAsync( _options.ScopeName );
        var collection = await scope.CollectionAsync( _options.CollectionName );
        
        try
        {
            var mutex = await collection.RequestMutexAsync( _options.MutexName, _options.MutexExpireInterval );
            mutex.AutoRenew( _options.MutexRenewInterval, _options.MutexMaxLifetime );
            return mutex;
        }
        catch ( CouchbaseLockUnavailableException ex )
        {
            throw new MigrationMutexUnavailableException( "The mutex is unavailable.", ex );
        }
    }

    public async Task InitializeAsync()
    {
        var cluster = await _clusterProvider.GetClusterAsync();

        // check for bucket

        var hasBucketResult = await cluster.QueryAsync<int>( "SELECT RAW count(*) FROM system: buckets WHERE name = 'Hyperbee'" )
            .ConfigureAwait( false );

        if ( await hasBucketResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            throw new MigrationException( "Missing bucket." );
        }

        // check for scope

        var hasScopeResult = await cluster.QueryAsync<int>( "SELECT RAW count(*) FROM system:scopes WHERE `bucket` = 'Hyperbee' AND name = 'migrations'" )
            .ConfigureAwait( false );

        if ( await hasScopeResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            await cluster.QueryAsync<dynamic>( "CREATE SCOPE `Hyperbee`.migrations" );
        }

        // check for collection

        var hasCollectionResult = await cluster.QueryAsync<int>( "SELECT RAW count(*) FROM system:keyspaces WHERE `bucket` = 'Hyperbee' AND `scope` = 'migrations' AND name = 'ledger'" )
            .ConfigureAwait( false );

        if ( await hasCollectionResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            await cluster.QueryAsync<dynamic>( "CREATE COLLECTION `Hyperbee`.`migrations`.`ledger`" );
        }

        // check for primary index

        var hasPrimaryIndexResult = await cluster.QueryAsync<int>( "SELECT * FROM system:indexes WHERE bucket_id = 'Hyperbee' AND scope_id = 'migrations' AND keyspace_id = 'ledger' AND is_primary" )
            .ConfigureAwait( false );

        if ( await hasPrimaryIndexResult.Rows.FirstOrDefaultAsync() == 0 )
        {
            await cluster.QueryAsync<dynamic>( "CREATE PRIMARY INDEX ON `default`:`Hyperbee`.`migrations`.`ledger`" );
        }
    }

    public async Task<IMigrationRecord> LoadAsync( string migrationId )
    {
        var collection = await GetCollectionAsync();
        var check = await collection.ExistsAsync( migrationId ).ConfigureAwait( false );

        if ( !check.Exists )
            return default;

        var result = await collection.GetAsync( migrationId ).ConfigureAwait( false );
        return result.ContentAs<MigrationRecord>();
    }

    public async Task DeleteAsync( IMigrationRecord record )
    {
        var collection = await GetCollectionAsync();
        await collection.RemoveAsync( record.Id ).ConfigureAwait( false );
    }

    public async Task StoreAsync( string migrationId )
    {
        var collection = await GetCollectionAsync();

        var record = new MigrationRecord
        {
            Id = migrationId
        };

        await collection.InsertAsync( migrationId, record ).ConfigureAwait( false );
    }
}