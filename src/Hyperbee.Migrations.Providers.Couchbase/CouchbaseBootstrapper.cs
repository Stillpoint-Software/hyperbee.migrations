using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Diagnostics;
using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Wait;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Couchbase;

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
        _logger?.LogInformation( "Waiting for system ready ..." );

        using var tts = TimeoutTokenSource.CreateTokenSource( timeout );
        using var lts = CancellationTokenSource.CreateLinkedTokenSource( tts.Token, cancellationToken );
        var operationCancelToken = lts.Token;

        var notifyInterval = GetNotifyInterval( timeout, 10 );

        try
        {
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
            if ( ex.CancellationToken == tts.Token )
                throw new UnambiguousTimeoutException( $"Timed out after {timeout!.Value.TotalSeconds}.", ex );

            throw;
        }
    }

    private async Task WaitForClusterAsync( TimeSpan notifyInterval, CancellationToken operationCancelToken )
    {
        // Calling GetClusterAsync() and WaitUntilReadyAsync() too early in the Couchbase
        // server initialization process results in unrecoverable client errors during
        // application initialization.
        //
        // This method addresses the following bootstrap issues:
        //
        // * GetClusterAsync() will fail if the management uri is not available
        //
        // * If the cluster is not in the `healthy` state GetClusterAsync() will
        //   initialize incorrectly as a pre-6.5 client. This will cause 
        //   WaitUntilReadyAsync() to fail with a NotSupportedException.
        //
        // * If you call WaitUntilReadyAsync immediately after the cluster reports
        //   itself as healthy, you will occasionally receive the 6.5
        //   NotSupportedException.
        //
        // When the 6.5 NotSupportedException is thrown the net client, and its internal
        // initialization are in an incorrect, and un-recoverable, state. There is no way
        // that we are aware of to reset the client without restarting the application.

        const int Start = 0;
        const int WaitForUri = 1;
        const int StateUriReady = 2;
        const int WaitForHealthy = 3;
        const int StateHealthy = 4;
        const int WaitForReady = 5;

        var state = Start;

        while ( true )
        {
            operationCancelToken.ThrowIfCancellationRequested();

            try
            {
                switch ( state )
                {
                    case Start:
                        {
                            _logger?.LogInformation( "Waiting for admin api ..." );
                            state = WaitForUri;
                            break;
                        }

                    case WaitForUri:
                        {
                            await _restApiService.WaitUntilManagementReadyAsync( notifyInterval, operationCancelToken ).ConfigureAwait( false );
                            _logger?.LogInformation( "Admin api is ready." );
                            state = StateUriReady;
                            break;
                        }

                    case StateUriReady:
                        {
                            _logger?.LogInformation( "Waiting for cluster ready ..." );
                            state = WaitForHealthy;
                            break;
                        }

                    case WaitForHealthy:
                        {
                            await _restApiService.WaitUntilClusterHealthyAsync( notifyInterval, operationCancelToken ).ConfigureAwait( false );
                            _logger?.LogInformation( "Cluster is healthy." );
                            state = StateHealthy;
                            break;
                        }

                    case StateHealthy:
                        {
                            // the cluster is healthy but calling `GetClusterAsync` too quickly results in
                            // an intermittent internal bootstrap error. when this happens, couchbase will
                            // throw a `NotSupportedException` with an incorrect complaint that the server
                            // version is < 6.5. this results in calls to WaitUntilReadyAsync() failing.
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
                // notify interval timeout
                _logger?.LogInformation( "Wait..." );
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
        }
    }

    private async Task WaitForBucketsAsync( TimeSpan notifyInterval, CancellationToken operationCancelToken )
    {
        // the cluster is ready but the buckets may not be.
        // wait for the buckets to initialize.

        _logger?.LogInformation( "Waiting for buckets..." );

        var cluster = await _clusterProvider.GetClusterAsync();

        foreach ( var (bucketName, _) in await cluster.Buckets.GetAllBucketsAsync() )
        {
            _logger?.LogInformation( "Waiting for bucket {bucketName} ...", bucketName );

            var bucket = await cluster.BucketAsync( bucketName );

            while ( true )
            {
                operationCancelToken.ThrowIfCancellationRequested();

                try
                {
                    await bucket.WaitUntilReadyAsync( notifyInterval ).ConfigureAwait( false );
                    _logger?.LogInformation( "Bucket {bucketName} is ready.", bucketName );
                    return;
                }
                catch ( UnambiguousTimeoutException )
                {
                    // notify interval timeout
                    _logger?.LogInformation( "Wait..." );
                }
            }
        }
    }

    private async Task SystemQueryWarmupAsync( CancellationToken operationCancelToken )
    {
        operationCancelToken.ThrowIfCancellationRequested();

        // the first select against `system:*` returns unpredictable results
        // after a hard shutdown. this is spooky but a sacrificial query
        // seems to fix it.

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync()
            .ConfigureAwait( false );

        var result = await clusterHelper.Cluster.QueryAsync<int>( "SELECT RAW count(*) FROM system:indexes WHERE is_primary" )
            .ConfigureAwait( false );

        var _ = await result.Rows.FirstOrDefaultAsync( operationCancelToken )
            .ConfigureAwait( false );
    }
}
