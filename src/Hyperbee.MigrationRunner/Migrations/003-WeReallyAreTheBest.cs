using Hyperbee.Migrations;
using Microsoft.Extensions.Logging;

namespace Hyperbee.MigrationRunner.Migrations;

[Migration(3)]
public class WeReallyAreTheBest : Migration
{
    private readonly ILogger<WeReallyAreTheBest> _logger;

    public WeReallyAreTheBest( ILogger<WeReallyAreTheBest> logger )
    {
        _logger = logger;
    }

    public override Task Up()
    {
        _logger.LogInformation( "Inside {name} `{direction}`", nameof(WeReallyAreTheBest), nameof(Up) );
        return Task.CompletedTask;
    }
}