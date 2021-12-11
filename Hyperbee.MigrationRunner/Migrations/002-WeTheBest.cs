using DnsClient.Internal;
using Hyperbee.Migrations;
using Microsoft.Extensions.Logging;

namespace Hyperbee.MigrationRunner.Migrations;

[Migration(2)]
public class WeTheBest : Migration
{
    private readonly ILogger<WeTheBest> _logger;

    public WeTheBest( ILogger<WeTheBest> logger )
    {
        _logger = logger;
    }

    public override void Up()
    {
        _logger.LogDebug( $"In Up `{nameof(WeTheBest)}`" );
    }
}