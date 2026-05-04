#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;

// Pluggable bootstrap step contract per ADR-0014.
//
// Steps run sequentially in the order registered with DI. Each step receives
// the shared BootstrapContext (cluster client, options, time provider, cancel
// token). Step implementations should be small, focused, and unit-testable
// against a mocked IOpenSearchClient.
//
// Consumers extend the bootstrapper by registering an additional IBootstrapStep
// implementation in DI. Reordering or replacing built-in steps is a deliberate
// extension point, not a casual override — document any non-default ordering
// in code.

public interface IBootstrapStep
{
    string Name { get; }

    Task<StepOutcome> ExecuteAsync( BootstrapContext context );
}
