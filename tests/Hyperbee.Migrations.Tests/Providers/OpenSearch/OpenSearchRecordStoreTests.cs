#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

[TestClass]
public class OpenSearchRecordStoreLockTuningTests
{
    private static OpenSearchRecordStore BuildStore( OpenSearchMigrationOptions options )
    {
        var client = Substitute.For<IOpenSearchClient>();
        var bootstrapper = new OpenSearchBootstrapper(
            Array.Empty<IBootstrapStep>(), client, options, TimeProvider.System, NullLoggerFactory.Instance );

        return new OpenSearchRecordStore(
            client, bootstrapper, options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );
    }

    [TestMethod]
    public void Ctor_WithValidLockTuning_DoesNotThrow()
    {
        var options = new OpenSearchMigrationOptions
        {
            LockRenewInterval = TimeSpan.FromSeconds( 30 ),
            LockStaleAfter = TimeSpan.FromSeconds( 60 ),
            LockMaxLifetime = TimeSpan.FromHours( 1 )
        };

        var act = () => BuildStore( options );

        act.Should().NotThrow();
    }

    [TestMethod]
    public void Ctor_NonPositiveRenewInterval_Throws()
    {
        var options = new OpenSearchMigrationOptions
        {
            LockRenewInterval = TimeSpan.Zero,
            LockStaleAfter = TimeSpan.FromSeconds( 60 ),
            LockMaxLifetime = TimeSpan.FromHours( 1 )
        };

        var act = () => BuildStore( options );

        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*LockRenewInterval*positive*" );
    }

    [TestMethod]
    public void Ctor_StaleAfterNotGreaterThanRenew_Throws()
    {
        var options = new OpenSearchMigrationOptions
        {
            LockRenewInterval = TimeSpan.FromSeconds( 30 ),
            LockStaleAfter = TimeSpan.FromSeconds( 30 ), // equal — not strictly greater
            LockMaxLifetime = TimeSpan.FromHours( 1 )
        };

        var act = () => BuildStore( options );

        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*LockStaleAfter*greater than*LockRenewInterval*" );
    }

    [TestMethod]
    public void Ctor_StaleAfterLessThan2xRenewInterval_Throws()
    {
        var options = new OpenSearchMigrationOptions
        {
            LockRenewInterval = TimeSpan.FromSeconds( 30 ),
            LockStaleAfter = TimeSpan.FromSeconds( 45 ), // > renew but < 2x renew
            LockMaxLifetime = TimeSpan.FromHours( 1 )
        };

        var act = () => BuildStore( options );

        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*at least*2 * LockRenewInterval*" );
    }

    [TestMethod]
    public void Ctor_MaxLifetimeNotGreaterThanStaleAfter_Throws()
    {
        var options = new OpenSearchMigrationOptions
        {
            LockRenewInterval = TimeSpan.FromSeconds( 30 ),
            LockStaleAfter = TimeSpan.FromSeconds( 60 ),
            LockMaxLifetime = TimeSpan.FromSeconds( 60 ) // equal — not greater
        };

        var act = () => BuildStore( options );

        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*LockMaxLifetime*greater than*LockStaleAfter*" );
    }

    [TestMethod]
    public void Ctor_DefaultOptions_PassValidation()
    {
        // The default values shipped on OpenSearchMigrationOptions must satisfy the
        // tuning rules. If a future default change violates them, the tests catch it.
        var options = new OpenSearchMigrationOptions();

        var act = () => BuildStore( options );

        act.Should().NotThrow();
    }

    [TestMethod]
    public void Ctor_ProductionDefaultsLikeTuning_PassValidation()
    {
        // Verify a representative production-style tuning passes (long renew, longer
        // stale-after, hour-scale max-lifetime). This catches accidental tightening
        // of the validator that would break production setups.
        var options = new OpenSearchMigrationOptions
        {
            LockRenewInterval = TimeSpan.FromSeconds( 60 ),
            LockStaleAfter = TimeSpan.FromMinutes( 5 ),
            LockMaxLifetime = TimeSpan.FromHours( 4 )
        };

        var act = () => BuildStore( options );

        act.Should().NotThrow();
    }
}
