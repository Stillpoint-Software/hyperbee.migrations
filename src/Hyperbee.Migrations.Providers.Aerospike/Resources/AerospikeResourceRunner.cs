using Aerospike.Client;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Aerospike.Resources;

public class AerospikeResourceRunner<TMigration>
    where TMigration : Migration
{
    private readonly IAsyncClient _client;
    private readonly ILogger _logger;

    public AerospikeResourceRunner(
        IAsyncClient client,
        ILogger<TMigration> logger )
    {
        _client = client;
        _logger = logger;
    }
}
