#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;

public enum BootstrapStatus
{
    Succeeded,
    Failed
}

// Public bootstrap result. Maps to the state-machine facade exposed via
// OpenSearchBootstrapper.RunAsync. The Steps projection is the diagnostic
// surface — operators can identify the failing step without log parsing.

public sealed record BootstrapResult(
    BootstrapStatus Status,
    IReadOnlyList<StepOutcome> Steps,
    StepOutcome? FailedAt
)
{
    public bool IsSuccess => Status == BootstrapStatus.Succeeded;
}
