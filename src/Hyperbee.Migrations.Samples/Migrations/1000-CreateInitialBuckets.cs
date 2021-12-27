using Hyperbee.Migrations.Couchbase.Resources;

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

        await _resourceRunner.CreateBucketsFromAsync( 
            "buckets.json",
            cancellationToken
        );

        await _resourceRunner.CreateStatementsFromAsync( new[]
            {
                "cloudc/statements.json",
                "wagglebee/statements.json",
                "wagglebeecache/statements.json"
            },
            cancellationToken
        );

        await _resourceRunner.CreateDocumentsFromAsync( new []
            {
                "cloudc/_default",
                "wagglebee/_default",
                "wagglebeecache/_default"
            },
            cancellationToken
        );
    }
}