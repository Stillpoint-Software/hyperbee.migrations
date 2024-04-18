using Couchbase.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hyperbee.MigrationRunner;

public class MainService: BackgroundService
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

    private static IEnumerable<T> GetEnumerable<T>( IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? Enumerable.Empty<T>();

    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        using var scope = _serviceProvider.CreateScope();

        var provider = scope.ServiceProvider;

        //TODO: MOVE couchbase requirements
        //var lifetime = provider.GetRequiredService<ICouchbaseLifetimeService>();

        await Task.Yield(); // yield to allow startup logs to write to console

        try
        {
            var config = provider.GetRequiredService<IConfiguration>();
            
            var nameAssembliesX =
                GetEnumerable<string>( config, "Migrations:FromAssemblies" )
                .ToList();

            var runner = provider.GetRequiredService<Migrations.MigrationRunner>();
            await runner.RunAsync( stoppingToken );
        }
        catch ( Exception ex )
        {
            _logger.LogCritical( ex, "Migrations encountered an unhandled exception." );
        }
        finally
        {
        //    if ( lifetime != null )
        //        await lifetime.CloseAsync();
        }

        _applicationLifetime.StopApplication();
    }
}