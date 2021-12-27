using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Diagnostics;
using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Couchbase.Services;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

internal interface ICouchbaseBootstrapper
{
    Task WaitForSystemReadyAsync( TimeSpan timeout );
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

    private static TimeSpan GetWaitInterval( TimeSpan timeout, int reportSeconds )
    {
        var timeoutSeconds = timeout.TotalSeconds;
        var intervalSeconds = Math.Min( timeoutSeconds, reportSeconds );

        return TimeSpan.FromSeconds( intervalSeconds );
    }

    public async Task WaitForSystemReadyAsync( TimeSpan timeout )
    {
        _logger?.LogInformation( "Waiting for system ready..." );

        // compute wait intervals
        var waitInterval = GetWaitInterval( timeout, 10 );

        // cancellation token
        using var tokenSource = new CancellationTokenSource();
        tokenSource.CancelAfter( timeout );
        var timeoutToken = tokenSource.Token;

        try
        {
            // wait for management uri
            await WaitForManagementUriAsync( waitInterval, timeoutToken )
                .ConfigureAwait( false );

            // wait for cluster
            await WaitForClusterAsync( waitInterval, timeoutToken )
                .ConfigureAwait( false );

            // wait for buckets
            await WaitForBucketsAsync( waitInterval, timeoutToken )
                .ConfigureAwait( false );

            // warm up n1ql
            await SystemQueryWarmupAsync( timeoutToken )
                .ConfigureAwait( false );
        }
        catch ( OperationCanceledException ex )
        {
            throw new UnambiguousTimeoutException( $"Timed out after {timeout.TotalSeconds}.", ex );
        }
    }

    private async Task WaitForManagementUriAsync( TimeSpan waitInterval, CancellationToken timeoutToken )
    {
        _logger?.LogInformation( "Waiting for management Uri..." );

        while ( true )
        {
            timeoutToken.ThrowIfCancellationRequested();

            try
            {
                await _restApiService.WaitUntilManagementReadyAsync( waitInterval, timeoutToken )
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

    private async Task WaitForClusterAsync( TimeSpan waitInterval, CancellationToken timeoutToken )
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
            timeoutToken.ThrowIfCancellationRequested();

            try
            {
                switch ( state )
                {
                    case WaitForHealthy:
                    {
                        await _restApiService.WaitUntilClusterHealthyAsync( waitInterval, timeoutToken ).ConfigureAwait( false );
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
                        // try and remedy by adding a delay until we can find a better solution.
                        
                        await Task.Delay( 5000, timeoutToken );
                        state = WaitForReady;
                        break;
                    }

                    case WaitForReady:
                    {
                        var cluster = await _clusterProvider.GetClusterAsync().ConfigureAwait( false );
                        var waitOptions = new WaitUntilReadyOptions().CancellationToken( timeoutToken );
                        await cluster.WaitUntilReadyAsync( waitInterval, waitOptions ).ConfigureAwait( false );
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
                // bootstrap initialization problem.

                throw new SystemException( "Couchbase is incorrectly reporting the system version as < 6.5.", ex );
            }

            _logger?.LogInformation( "Wait..." );
        }
    }

    private async Task WaitForBucketsAsync( TimeSpan waitInterval, CancellationToken timeoutToken )
    {
        _logger?.LogInformation( "Waiting for buckets..." );

        var cluster = await _clusterProvider.GetClusterAsync();

        foreach( var (bucketName, _) in await cluster.Buckets.GetAllBucketsAsync() )
        {
            _logger?.LogInformation( "Waiting for bucket {bucketName}...", bucketName );

            var bucket = await cluster.BucketAsync( bucketName );

            while ( true )
            {
                timeoutToken.ThrowIfCancellationRequested();

                try
                {
                    await bucket.WaitUntilReadyAsync( waitInterval )
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

    private async Task SystemQueryWarmupAsync( CancellationToken timeoutToken )
    {
        timeoutToken.ThrowIfCancellationRequested();

        // the first select against `system:*` returns unpredictable results
        // after hard shutdown. this is spooky but a sacrificial query seems
        // to fix it.

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync()
            .ConfigureAwait( false );

        var result = await clusterHelper.Cluster.QueryAsync<int>( "SELECT RAW count(*) FROM system:indexes WHERE is_primary" )
            .ConfigureAwait( false );

        var _ = await result.Rows.FirstOrDefaultAsync( timeoutToken )
            .ConfigureAwait( false );
    }
}