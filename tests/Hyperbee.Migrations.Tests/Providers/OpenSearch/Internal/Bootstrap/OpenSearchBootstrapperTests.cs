#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal.Bootstrap;

[TestClass]
public class OpenSearchBootstrapperTests
{
    private static OpenSearchBootstrapper BuildBootstrapper( params IBootstrapStep[] steps )
    {
        var client = Substitute.For<IOpenSearchClient>();
        var options = new OpenSearchMigrationOptions();
        return new OpenSearchBootstrapper(
            steps,
            client,
            options,
            TimeProvider.System,
            NullLoggerFactory.Instance );
    }

    private sealed class StubStep : IBootstrapStep
    {
        public string Name { get; }
        public StepStatus ResultStatus { get; }
        public int CallCount { get; private set; }

        public StubStep( string name, StepStatus result )
        {
            Name = name;
            ResultStatus = result;
        }

        public Task<StepOutcome> ExecuteAsync( BootstrapContext context )
        {
            CallCount++;
            return Task.FromResult( ResultStatus switch
            {
                StepStatus.Succeeded => StepOutcome.Succeeded( Name, TimeSpan.Zero ),
                StepStatus.Skipped => StepOutcome.Skipped( Name, TimeSpan.Zero ),
                _ => StepOutcome.Failed( Name, TimeSpan.Zero, new InvalidOperationException( "stub failure" ) )
            } );
        }
    }

    [TestMethod]
    public async Task RunAsync_AllStepsSucceed_ReturnsSuccessWithEveryOutcome()
    {
        var s1 = new StubStep( "step-1", StepStatus.Succeeded );
        var s2 = new StubStep( "step-2", StepStatus.Succeeded );
        var s3 = new StubStep( "step-3", StepStatus.Succeeded );

        var bootstrapper = BuildBootstrapper( s1, s2, s3 );

        var result = await bootstrapper.RunAsync();

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be( BootstrapStatus.Succeeded );
        result.Steps.Should().HaveCount( 3 );
        result.FailedAt.Should().BeNull();
        s1.CallCount.Should().Be( 1 );
        s2.CallCount.Should().Be( 1 );
        s3.CallCount.Should().Be( 1 );
    }

    [TestMethod]
    public async Task RunAsync_StepFails_HaltsAndReportsFailedAt()
    {
        var s1 = new StubStep( "step-1", StepStatus.Succeeded );
        var s2 = new StubStep( "step-2", StepStatus.Failed );
        var s3 = new StubStep( "step-3", StepStatus.Succeeded );

        var bootstrapper = BuildBootstrapper( s1, s2, s3 );

        var result = await bootstrapper.RunAsync();

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be( BootstrapStatus.Failed );
        result.Steps.Should().HaveCount( 2 ); // s3 was not invoked
        result.FailedAt.Should().NotBeNull();
        result.FailedAt!.Name.Should().Be( "step-2" );
        s3.CallCount.Should().Be( 0 );
    }

    [TestMethod]
    public async Task RunAsync_NoSteps_ReturnsImmediateSuccess()
    {
        var bootstrapper = BuildBootstrapper();

        var result = await bootstrapper.RunAsync();

        result.IsSuccess.Should().BeTrue();
        result.Steps.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RunAsync_CancellationRequested_PropagatesOperationCanceled()
    {
        var s1 = new StubStep( "step-1", StepStatus.Succeeded );
        var s2 = new StubStep( "step-2", StepStatus.Succeeded );
        var bootstrapper = BuildBootstrapper( s1, s2 );

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await bootstrapper.RunAsync( cts.Token );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task RunAsync_FailedStep_CarriesExceptionInOutcome()
    {
        var s1 = new StubStep( "step-1", StepStatus.Failed );
        var bootstrapper = BuildBootstrapper( s1 );

        var result = await bootstrapper.RunAsync();

        result.FailedAt!.Exception.Should().NotBeNull();
        result.FailedAt.Exception!.Message.Should().Be( "stub failure" );
    }
}

[TestClass]
public class StepOutcomeTests
{
    [TestMethod]
    public void Succeeded_FactoryProducesExpectedShape()
    {
        var outcome = StepOutcome.Succeeded( "x", TimeSpan.FromSeconds( 1 ), "ok" );

        outcome.Status.Should().Be( StepStatus.Succeeded );
        outcome.Detail.Should().Be( "ok" );
        outcome.Exception.Should().BeNull();
    }

    [TestMethod]
    public void Failed_FactoryCarriesException()
    {
        var ex = new InvalidOperationException( "boom" );
        var outcome = StepOutcome.Failed( "x", TimeSpan.Zero, ex, "context" );

        outcome.Status.Should().Be( StepStatus.Failed );
        outcome.Exception.Should().Be( ex );
        outcome.Detail.Should().Be( "context" );
    }
}
