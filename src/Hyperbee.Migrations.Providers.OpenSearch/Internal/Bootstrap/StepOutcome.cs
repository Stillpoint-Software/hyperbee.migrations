#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;

public enum StepStatus
{
    Succeeded,
    Skipped,
    Failed
}

// Per-step outcome surfaced via BootstrapResult.Steps so operators can see
// exactly which step failed and what it tried — without parsing log strings.

public sealed record StepOutcome(
    string Name,
    StepStatus Status,
    TimeSpan Duration,
    string? Detail = null,
    Exception? Exception = null
)
{
    public static StepOutcome Succeeded( string name, TimeSpan duration, string? detail = null )
        => new( name, StepStatus.Succeeded, duration, detail );

    public static StepOutcome Skipped( string name, TimeSpan duration, string? detail = null )
        => new( name, StepStatus.Skipped, duration, detail );

    public static StepOutcome Failed( string name, TimeSpan duration, Exception exception, string? detail = null )
        => new( name, StepStatus.Failed, duration, detail, exception );
}
