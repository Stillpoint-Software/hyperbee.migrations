using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Diagnostics;
using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Couchbase.Services;
using Hyperbee.Migrations.Couchbase.Wait;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

internal interface ICouchbaseBootstrapper
{
    Task WaitForSystemReadyAsync( TimeSpan? timeout, CancellationToken cancellationToken = default );
}

internal class CouchbaseBootstrapper : ICouchbaseBootstrapper
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ICouchbaseRestApiService _restApiService;
    private readonly ILogger<CouchbaseBootstrapper> _logger;

    public CouchbaseBootstrapper( IClusterProvider clusterProvider, ICouchbaseRestApiService restApiService, ILogger<CouchbaseBootstrapper> logger )
    {
        _clusterProvider = clusterProvider;
        _restApiService = restApiService;
        _logger = logger;
    }

    private static TimeSpan GetNotifyInterval( TimeSpan? timeout, int reportSeconds )
    {
        var timeoutSeconds = timeout?.TotalSeconds ?? double.MaxValue;
        var intervalSeconds = Math.Min( timeoutSeconds, reportSeconds );

        return TimeSpan.FromSeconds( intervalSeconds );
    }

    public async Task WaitForSystemReadyAsync( TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        _logger?.LogInformation( "Waiting for system ready..." );

        var tokenProvider = new TimeoutTokenProvider( timeout, cancellationToken );
        var operationCancelToken = tokenProvider.Token;
        var notifyInterval = GetNotifyInterval( timeout, 10 );

        try
        {
            // wait for management uri
            await WaitForManagementUriAsync( notifyInterval, operationCancelToken )
                .ConfigureAwait( false );

            // wait for cluster
            await WaitForClusterAsync( notifyInterval, operationCancelToken )
                .ConfigureAwait( false );

            // wait for buckets
            await WaitForBucketsAsync( notifyInterval, operationCancelToken )
                .ConfigureAwait( false );

            // warm up n1ql
            await SystemQueryWarmupAsync( operationCancelToken )
                .ConfigureAwait( false );
        }
        catch ( OperationCanceledException ex )
        {
            if ( ex.CancellationToken == tokenProvider.TimeoutToken )
                throw new UnambiguousTimeoutException( $"Timed out after {timeout!.Value.TotalSeconds}.", ex );

            throw;
        }
    }

    private async Task WaitForManagementUriAsync( TimeSpan notifyInterval, CancellationToken operationCancelToken )
    {
        _logger?.LogInformation( "Waiting for management Uri..." );

        while ( true )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            try
            {
                await _restApiService.WaitUntilManagementReadyAsync( notifyInterval, operationCancelToken )
                    .ConfigureAwait( false );

                _logger?.LogInformation( "Management Uri is ready." );
                return;
            }
            catch ( UnambiguousTimeoutException )
            {
                // wait interval timeout
            }

            _logger?.LogInformation( "Wait..." );
        }
    }

    private async Task WaitForClusterAsync( TimeSpan notifyInterval, CancellationToken operationCancelToken )
    {
        // calling the client `cluster.WaitUntilReadyAsync()` is not sufficient because
        // the cluster is considered ready as soon as it is ping-able. cluster buckets
        // may not be ready.
        //
        // calling the client `bucket.WaitUntilReadyAsync()` is also problematic because 
        // you must first call `cluster.BucketAsync( name )` which does not take a timeout
        // or a cancellation token and will block indefinitely if the cluster is not ready.
        //
        // a simpler strategy is to wait for the cluster status to transition to `healthy`
        // for all active nodes. we can get this status from the rest api.

        _logger?.LogInformation( "Waiting for cluster..." );

        const int WaitForHealthy = 0;
        const int WaitForMoment = 1;
        const int WaitForReady = 2;

        var state = WaitForHealthy;

        while ( true )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            try
            {
                switch ( state )
                {
                    case WaitForHealthy:
                    {
                        await _restApiService.WaitUntilClusterHealthyAsync( notifyInterval, operationCancelToken ).ConfigureAwait( false );
                        state = WaitForMoment;
                        _logger?.LogInformation( "Cluster is healthy." );
                        break;
                    }

                    case WaitForMoment:
                    {
                        // the cluster is healthy but calling `GetClusterAsync` too quickly results in
                        // an intermittent internal bootstrap error. when this happens, couchbase will
                        // incorrectly throw `NotSupportedException`s with an incorrect complaint that
                        // the server version is < 6.5. this results in calls to `WaitUntilReadyAsync`
                        // failing.
                        //
                        // try to remedy by adding a delay until we can find a better solution.
                        
                        await Task.Delay( 5000, operationCancelToken );
                        state = WaitForReady;
                        break;
                    }

                    case WaitForReady:
                    {
                        var cluster = await _clusterProvider.GetClusterAsync().ConfigureAwait( false );
                        var waitOptions = new WaitUntilReadyOptions().CancellationToken( operationCancelToken );
                        await cluster.WaitUntilReadyAsync( notifyInterval, waitOptions ).ConfigureAwait( false );
                        _logger?.LogInformation( "Cluster is ready." );
                        return;
                    }
                }
            }
            catch ( UnambiguousTimeoutException )
            {
                // wait interval timeout
            }
            catch ( NotSupportedException ex )
            {
                // the cluster is healthy but WaitUntilReadyAsync failed due to a couchbase
                // bootstrap initialization problem or invalid credentials.

                _logger?.LogCritical( 
                    "Couchbase incorrectly reported the system version as < 6.5. " +
                    "This is caused by invalid credentials or is the result of an internal couchbase bootstrap error. " +
                    "The system will exit with error." );

                throw new SystemException( "Couchbase incorrectly reported the system version as < 6.5.", ex );
            }

            _logger?.LogInformation( "Wait..." );
        }
    }

    private async Task WaitForBucketsAsync( TimeSpan notifyInterval, CancellationToken operationCancelToken )
    {
        _logger?.LogInformation( "Waiting for buckets..." );

        var cluster = await _clusterProvider.GetClusterAsync();

        foreach( var (bucketName, _) in await cluster.Buckets.GetAllBucketsAsync() )
        {
            _logger?.LogInformation( "Waiting for bucket {bucketName}...", bucketName );

            var bucket = await cluster.BucketAsync( bucketName );

            while ( true )
            {
                operationCancelToken.ThrowIfCancellationRequested();

                try
                {
                    await bucket.WaitUntilReadyAsync( notifyInterval )
                        .ConfigureAwait( false );

                    _logger?.LogInformation( "Bucket {bucketName} is ready.", bucketName );
                    return;
                }
                catch ( UnambiguousTimeoutException )
                {
                    // wait interval timeout
                }

                _logger?.LogInformation( "Wait..." );
            }
        }
    }

    private async Task SystemQueryWarmupAsync( CancellationToken operationCancelToken )
    {
        operationCancelToken.ThrowIfCancellationRequested();

        // the first select against `system:*` returns unpredictable results
        // after hard shutdown. this is spooky but a sacrificial query seems
        // to fix it.

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync()
            .ConfigureAwait( false );

        var result = await clusterHelper.Cluster.QueryAsync<int>( "SELECT RAW count(*) FROM system:indexes WHERE is_primary" )
            .ConfigureAwait( false );

        var _ = await result.Rows.FirstOrDefaultAsync( operationCancelToken )
            .ConfigureAwait( false );
    }
}