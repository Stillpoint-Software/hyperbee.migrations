using Hyperbee.Migrations.Couchbase;
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

        _resourceRunner.WaitSettings = new WaitSettings( TimeSpan.FromSeconds( 3 ), 20 );

        await _resourceRunner.CreateBucketsFromAsync(
            "buckets.json"
        );

        await _resourceRunner.CreateStatementsFromAsync(
            "cloudc/statements.json",
            "wagglebee/statements.json",
            "wagglebeecache/statements.json"
        );

        await _resourceRunner.CreateDocumentsFromAsync(
            "cloudc/_default",
            "wagglebee/_default",
            "wagglebeecache/_default"
        );
    }
}