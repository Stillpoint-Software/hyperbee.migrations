using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hyperbee.MigrationRunner.Postgres;

public class MainService : BackgroundService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<MainService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public MainService( IServiceProvider serviceProvider, IHostApplicationLifetime applicationLifetime, ILogger<MainService> logger )
    {
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        using var scope = _serviceProvider.CreateScope();

        var provider = scope.ServiceProvider;

        await Task.Yield(); // yield to allow startup logs to write to console

        try
        {
            var runner = provider.GetRequiredService<Migrations.MigrationRunner>();
            await runner.RunAsync( stoppingToken );
        }
        catch ( Exception ex )
        {
            _logger.LogCritical( ex, "Migrations encountered an unhandled exception." );
        }

        _applicationLifetime.StopApplication();
    }
}
