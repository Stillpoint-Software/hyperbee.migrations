using Hyperbee.Migrations.Providers.Couchbase.Resources;

namespace Hyperbee.Migrations.Samples.Migrations;

[Migration(1000)] 
public class CreateInitialBuckets : Migration
{
    private readonly CouchbaseResourceRunner<CreateInitialBuckets> _resourceRunner;

    public CreateInitialBuckets( CouchbaseResourceRunner<CreateInitialBuckets> resourceRunner )
    {
        _resourceRunner = resourceRunner;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial buckets and state.
        // resource migrations are atypical; prefer `n1ql` migrations.

        await _resourceRunner.StatementsFromAsync( new[]
            {
                "statements.json",
                "migrationbucket/statements.json"
            },
            cancellationToken
        );

        await _resourceRunner.DocumentsFromAsync( new []
            {
                "migrationbucket/_default"
            },
            cancellationToken
        );
    }
}