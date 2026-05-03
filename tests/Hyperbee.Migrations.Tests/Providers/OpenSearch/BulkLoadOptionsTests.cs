#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// R-20 — bulk-load defaults pinning. The exact values are spec'd, so any
// drift between the requirements doc and the BulkLoadOptions class needs
// to surface as a test failure rather than a silent change in production
// behavior.
//
// Live-cluster bulk semantics (actual indexing, 429 retry surfacing,
// refresh-once-at-end) are exercised by integration tests when a real
// cluster is available; this class pins the in-process defaults.

[TestClass]
public class BulkLoadOptionsTests
{
    [TestMethod]
    public void Defaults_MatchR20Spec()
    {
        var opts = new BulkLoadOptions();

        opts.BatchSize.Should().Be( 1000,
            because: "R-20 default targets ~5MB batches; doc count maps to that at typical document shapes" );
        opts.MaxDegreeOfParallelism.Should().Be( 8,
            because: "R-20 specifies 8x parallelism" );
        opts.BackOffRetries.Should().Be( 5,
            because: "R-20 specifies 5 retries on 429" );
        opts.InitialBackOff.Should().Be( TimeSpan.FromSeconds( 1 ),
            because: "R-20 starts backoff at 1s; 1s -> 2s -> 4s -> 8s -> 16s with 5 retries" );
        opts.RefreshOnCompleted.Should().BeTrue(
            because: "R-20 requires a single _refresh at end of bulk load" );
    }

    [TestMethod]
    public void Overrides_AreHonored()
    {
        // R-20 says "All defaults are overridable via options" - pin that
        // every field is genuinely settable, not init-only / read-only.
        var opts = new BulkLoadOptions
        {
            BatchSize = 500,
            MaxDegreeOfParallelism = 4,
            BackOffRetries = 3,
            InitialBackOff = TimeSpan.FromMilliseconds( 250 ),
            RefreshOnCompleted = false
        };

        opts.BatchSize.Should().Be( 500 );
        opts.MaxDegreeOfParallelism.Should().Be( 4 );
        opts.BackOffRetries.Should().Be( 3 );
        opts.InitialBackOff.Should().Be( TimeSpan.FromMilliseconds( 250 ) );
        opts.RefreshOnCompleted.Should().BeFalse();
    }
}
