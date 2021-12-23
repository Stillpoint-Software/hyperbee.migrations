using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Couchbase.Services;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Samples.Migrations;

[Migration(2000)] 
public class SecondaryAction : Migration
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger<CreateInitialBuckets> _logger;
    private readonly ICouchbaseRestApiService _restApiService;

    public SecondaryAction( IClusterProvider clusterProvider, ILogger<CreateInitialBuckets> logger, ICouchbaseRestApiService restApiService )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
        _restApiService = restApiService;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        try
        {
            var result = await _restApiService.GetClusterDetailsAsync( cancellationToken );
        }
        catch ( Exception ex )
        {
            var m = ex.Message;
        }

        // N1Ql migration
        await Task.CompletedTask;
    }
}