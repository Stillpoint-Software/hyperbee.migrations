using Couchbase.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hyperbee.MigrationRunner;

public class MigrationRunnerService: BackgroundService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<MigrationRunnerService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public MigrationRunnerService( IServiceProvider serviceProvider, IHostApplicationLifetime applicationLifetime, ILogger<MigrationRunnerService> logger )
    {
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        using var scope = _serviceProvider.CreateScope();

        var provider = scope.ServiceProvider;
        var lifetime = provider.GetRequiredService<ICouchbaseLifetimeService>();

        try
        {
            var runner = provider.GetRequiredService<Migrations.MigrationRunner>();
            await runner.RunAsync( stoppingToken );
        }
        catch ( Exception ex )
        {
            _logger.LogCritical( ex, "Migrations encountered an unhandled exception." );
        }
        finally
        {
            if ( lifetime != null )
                await lifetime.CloseAsync();
        }

        _applicationLifetime.StopApplication();
    }
}