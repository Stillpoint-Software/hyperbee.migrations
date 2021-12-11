using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Samples.Migrations;

[Migration(2)]
public class WeTheBest : Migration
{
    private readonly ILogger _logger;

    public WeTheBest( ILogger<WeTheBest> logger )
    {
        _logger = logger;
    }

    public override Task UpAsync( CancellationToken cancellationToken = default )
    {
        _logger.LogInformation( "Inside {name} `{direction}`", nameof(WeTheBest), nameof(UpAsync) );
        return Task.CompletedTask;
    }
}