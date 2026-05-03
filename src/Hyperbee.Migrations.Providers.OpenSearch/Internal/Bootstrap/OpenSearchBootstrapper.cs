#nullable enable
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;

// State-machine facade over an IBootstrapStep[] pipeline (ADR-0014).
//
// Public contract:
//   var result = await bootstrapper.RunAsync(ct);
//   if (!result.IsSuccess) ... // FailedAt names the step that failed
//
// Internal: runs each step sequentially; on first failure, halts and returns.
// The cancellation token propagates to every step; an OperationCanceledException
// from any step short-circuits the pipeline.

public sealed class OpenSearchBootstrapper
{
    private readonly IReadOnlyList<IBootstrapStep> _steps;
    private readonly IOpenSearchClient _client;
    private readonly OpenSearchMigrationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenSearchBootstrapper> _logger;

    public OpenSearchBootstrapper(
        IEnumerable<IBootstrapStep> steps,
        IOpenSearchClient client,
        OpenSearchMigrationOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory )
    {
        _steps = steps.ToList();
        _client = client;
        _options = options;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OpenSearchBootstrapper>();
    }

    public async Task<BootstrapResult> RunAsync( CancellationToken cancellationToken = default )
    {
        var context = new BootstrapContext
        {
            Client = _client,
            Options = _options,
            TimeProvider = _timeProvider,
            LoggerFactory = _loggerFactory,
            CancellationToken = cancellationToken
        };

        var outcomes = new List<StepOutcome>( _steps.Count );

        _logger.LogInformation( "Bootstrapper starting with {count} step(s).", _steps.Count );

        foreach ( var step in _steps )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await step.ExecuteAsync( context ).ConfigureAwait( false );
            outcomes.Add( outcome );

            if ( outcome.Status == StepStatus.Failed )
            {
                _logger.LogError(
                    outcome.Exception,
                    "Bootstrapper failed at step {step}: {detail}",
                    outcome.Name, outcome.Detail ?? "(no detail)" );

                return new BootstrapResult( BootstrapStatus.Failed, outcomes, outcome );
            }
        }

        _logger.LogInformation( "Bootstrapper completed successfully ({count} step(s)).", outcomes.Count );
        return new BootstrapResult( BootstrapStatus.Succeeded, outcomes, FailedAt: null );
    }
}
