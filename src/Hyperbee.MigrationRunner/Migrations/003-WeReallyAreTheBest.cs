using Hyperbee.Migrations;
using Microsoft.Extensions.Logging;

namespace Hyperbee.MigrationRunner.Migrations;

[Migration(3)]
public class WeReallyAreTheBest : Migration
{
    private readonly ILogger _logger;

    public WeReallyAreTheBest( ILogger<WeReallyAreTheBest> logger )
    {
        _logger = logger;
    }

    public override Task UpAsync( CancellationToken cancellationToken = default )
    {
        _logger.LogInformation( "Inside {name} `{direction}`", nameof(WeReallyAreTheBest), nameof(UpAsync) );
        return Task.CompletedTask;
    }
}