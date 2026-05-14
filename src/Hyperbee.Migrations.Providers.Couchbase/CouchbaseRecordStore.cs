using System;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.Extensions.DependencyInjection;
using Couchbase.Extensions.Locks;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;
using Couchbase.Query;
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

        // wait for the N1QL planner's keyspace-to-datastore mapping to
        // catch up. system:keyspaces (the previous wait) and the planner's
        // datastore-mapping are SEPARATE metadata caches; on containerized
        // Couchbase Server the planner can lag the catalog by 30s-2min,
        // and CREATE PRIMARY INDEX is one of the operations that surfaces
        // the lag as IndexFailureException 12021 "Scope not found in CB
        // datastore". Probe by issuing a SELECT against the actual
        // keyspace -- if the planner can compile that, CREATE INDEX will
        // succeed.

        try
        {
            _logger.LogInformation( "Waiting for N1QL planner readiness on ledger collection..." );

            await WaitHelper.WaitUntilAsync(
                async _ => await clusterHelper.CollectionPlannerReadyAsync( bucketName, scopeName, collectionName ).ConfigureAwait( false ),
                TimeSpan.FromMinutes( 3 ), // wide upper bound: 7.0.2 community in CI containers needs this
                new PauseRetryStrategy( TimeSpan.FromSeconds( 1 ) ),
                cancellationToken
            );

            _logger.LogInformation( "N1QL planner ready for ledger collection." );
        }
        catch ( Exception ex )
        {
            _logger.LogWarning( ex, "N1QL planner readiness check timed out for ledger collection `{bucketName}`.`{scopeName}`.`{collectionName}`. Proceeding anyway -- CREATE INDEX will retry.", bucketName, scopeName, collectionName );
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

    public async Task<MigrationRecord> ReadAsync( string recordId )
    {
        var collection = await GetCollectionAsync()
            .ConfigureAwait( false );

        var check = await collection.ExistsAsync( recordId )
            .ConfigureAwait( false );

        if ( !check.Exists )
            return null;

        var result = await collection.GetAsync( recordId )
            .ConfigureAwait( false );

        var record = result.ContentAs<MigrationRecord>();
        record?.EnsureLedgerIntegrity();
        return record;
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

    public async Task<IReadOnlySet<string>> IntersectWithAppliedAsync(
        IEnumerable<string> candidateIds,
        CancellationToken cancellationToken = default )
    {
        if ( candidateIds == null )
            throw new ArgumentNullException( nameof( candidateIds ) );

        var ids = candidateIds as string[] ?? candidateIds.ToArray();
        var found = new HashSet<string>( StringComparer.Ordinal );
        if ( ids.Length == 0 )
            return found;

        // R-16: single N1QL `USE KEYS [...]` round-trip instead of N parallel
        // ExistsAsync fan-out. Fan-out scaled candidate-set size linearly with
        // open KV ops -- a 500-migration squash auto-mark opened 500 concurrent
        // KV connections, risking throttle/retry storms on smaller clusters.
        // USE KEYS is a primary-key index hit at the cluster, so the query
        // engine returns only the subset of ids that resolve in the keyspace
        // -- semantically identical, one round-trip, no fan-out.
        var cluster = await _clusterProvider.GetClusterAsync().ConfigureAwait( false );

        var (bucketName, scopeName, collectionName) = _options;
        var keyspace = $"`{bucketName}`.`{scopeName}`.`{collectionName}`";

        var queryOptions = new QueryOptions()
            .Parameter( "ids", ids )
            .CancellationToken( cancellationToken );

        var statement =
            $"SELECT RAW META(d).id FROM {keyspace} d USE KEYS $ids";

        var result = await cluster.QueryAsync<string>( statement, queryOptions )
            .ConfigureAwait( false );

        await foreach ( var hit in result.WithCancellation( cancellationToken ).ConfigureAwait( false ) )
        {
            if ( !string.IsNullOrEmpty( hit ) )
                found.Add( hit );
        }

        return found;
    }

    public async Task<IReadOnlySet<long>> IntersectWithSquashedAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken = default )
    {
        if ( versions == null )
            throw new ArgumentNullException( nameof( versions ) );

        var inputs = versions as long[] ?? versions.ToArray();
        var covered = new HashSet<long>();
        if ( inputs.Length == 0 )
            return covered;

        // Transitive squash satisfaction (ADR-0019 A6). N1QL UNNEST flattens the
        // replaces arrays; the IN-list filter restricts to versions the caller asked
        // about. RequestPlus consistency ensures we see writes from this runner's
        // own prior journal mutations within the same lock window.
        var clusterHelper = await _clusterProvider.GetClusterHelperAsync().ConfigureAwait( false );
        var cluster = clusterHelper.Cluster;

        var (bucket, scope, collection) = _options;
        var keyspace = $"`{bucket}`.`{scope}`.`{collection}`";

        var query =
            $"SELECT DISTINCT v FROM {keyspace} AS m UNNEST m.replaces AS v " +
            "WHERE m.kind = 1 AND v IN $versions";

        var options = new QueryOptions()
            .Parameter( "versions", inputs )
            .ScanConsistency( QueryScanConsistency.RequestPlus )
            .CancellationToken( cancellationToken );

        var result = await cluster.QueryAsync<long>( query, options ).ConfigureAwait( false );
        await foreach ( var v in result.ConfigureAwait( false ) )
            covered.Add( v );

        return covered;
    }

    public async Task<WriteOutcome> WriteAsync(
        MigrationRecord record,
        WritePrecondition precondition = WritePrecondition.None,
        CancellationToken cancellationToken = default )
    {
        if ( record == null )
            throw new ArgumentNullException( nameof( record ) );

        record.EnsureLedgerIntegrity();

        _logger.LogDebug( "Running {action} (record-bearing) with `{recordId}` precondition={precondition}",
            nameof( WriteAsync ), record.Id, precondition );

        var collection = await GetCollectionAsync().ConfigureAwait( false );

        if ( precondition == WritePrecondition.MustNotExist )
        {
            try
            {
                await collection.InsertAsync( record.Id, record,
                    options => options.CancellationToken( cancellationToken ) ).ConfigureAwait( false );
                return WriteOutcome.Created;
            }
            catch ( DocumentExistsException )
            {
                var existing = await ReadAsync( record.Id ).ConfigureAwait( false );
                if ( existing != null && string.Equals( existing.Checksum, record.Checksum, StringComparison.Ordinal ) )
                    return WriteOutcome.AlreadyExistsBenign;
                return WriteOutcome.PreconditionFailed;
            }
        }

        await collection.UpsertAsync( record.Id, record,
            options => options.CancellationToken( cancellationToken ) ).ConfigureAwait( false );
        return WriteOutcome.Created;
    }
}
