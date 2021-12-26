using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Couchbase.Services;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

internal interface ICouchbaseStartupWaiter
{
    Task WaitForSystemReadyAsync( TimeSpan timeout );
}

internal class CouchbaseStartupWaiter : ICouchbaseStartupWaiter
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ICouchbaseRestApiService _restApiService;
    private readonly ILogger<CouchbaseStartupWaiter> _logger;

    public CouchbaseStartupWaiter( IClusterProvider clusterProvider, ICouchbaseRestApiService restApiService, ILogger<CouchbaseStartupWaiter> logger )
    {
        _clusterProvider = clusterProvider;
        _restApiService = restApiService;
        _logger = logger;
    }

    private static TimeSpan GetConnectInterval( TimeSpan timeout, int reportSeconds )
    {
        var timeoutSeconds = timeout.TotalSeconds;

        const int MinimumIntervalSeconds = 15;

        // connects are expensive.
        // if the timeout - target is less than the minimum, expand the connect window to the timeout.

        var intervalSeconds = timeoutSeconds > reportSeconds && timeoutSeconds - reportSeconds < MinimumIntervalSeconds 
            ? timeoutSeconds 
            : Math.Min( timeoutSeconds, reportSeconds );

        return TimeSpan.FromSeconds( intervalSeconds );
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
        var connectInterval = GetConnectInterval( timeout, 10 );
        var waitInterval = GetWaitInterval( timeout, 5 );

        // cancellation token
        using var tokenSource = new CancellationTokenSource();
        tokenSource.CancelAfter( timeout );
        var timeoutToken = tokenSource.Token;

        try
        {
            // wait for management uri
            await WaitForManagementUriAsync( connectInterval, timeoutToken )
                .ConfigureAwait( false );

            // wait for cluster
            await WaitForClusterAsync( waitInterval, timeoutToken )
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
                await _restApiService.WaitUntilManagementReadyAsync( waitInterval )
                    .ConfigureAwait( false );

                _logger?.LogInformation( "Management Uri is ready." );
                return;
            }
            catch ( UnambiguousTimeoutException )
            {
                // reporting interval timeout
            }

            _logger?.LogInformation( "Wait..." );
        }
    }

    private async Task WaitForClusterAsync( TimeSpan waitInterval, CancellationToken timeoutToken )
    {
        _logger.LogInformation( "Waiting for cluster..." );

        while ( true )
        {
            timeoutToken.ThrowIfCancellationRequested();

            var result = await _restApiService.GetClusterDetailsAsync( timeoutToken )
                .ConfigureAwait( false );

            var status = result!["nodes"]!.AsArray()
                .Where( x => x["clusterMembership"]?.ToString() == "active" )
                .Select( x => x["status"]?.ToString() )
                .Where( x => x != null )
                .ToList();

            if ( status.All( x => x == "healthy" ) ) // states: warmup, healthy, ??
                break;

            _logger.LogInformation( "Wait..." );

            await Task.Delay( waitInterval, timeoutToken )
                .ConfigureAwait( false );
        }

        _logger?.LogInformation( "Cluster is ready." );
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