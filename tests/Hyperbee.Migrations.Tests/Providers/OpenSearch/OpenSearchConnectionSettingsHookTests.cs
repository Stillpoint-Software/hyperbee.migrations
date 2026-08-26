#nullable enable
using System.Reflection;
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Aws;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;
using AwsExtensions = Hyperbee.Migrations.Providers.OpenSearch.Aws.ServiceCollectionExtensions;
using CoreExtensions = Hyperbee.Migrations.Providers.OpenSearch.ServiceCollectionExtensions;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// ADR-0030 — the consumer escape hatch over ConnectionSettings.
//
// Both client factories previously exposed no way to reach the underlying
// ConnectionSettings. The only escape was to stop calling them and hand-roll the
// registration, which meant forking the auth wiring and the AWS-endpoint guard
// along with it. `configureSettings` closes that, and runs LAST so a consumer can
// override anything the library set.
//
// Two properties matter and are both pinned here:
//
//   1. The hook actually reaches the client the container hands out.
//   2. The hook cannot silently break the ledger. The ledger index carries a
//      strict mapping with camelCase fields; a hook that changes field-name
//      inference is rejected at registration with a pointed message rather than
//      surfacing later as strict_dynamic_mapping_exception on first write.

[TestClass]
public class OpenSearchConnectionSettingsHookTests
{
    private static readonly Uri Endpoint = new( "http://localhost:9200" );
    private static readonly Uri AwsEndpoint = new( "https://my-domain.us-east-1.es.amazonaws.com" );

    private static IOpenSearchClient Resolve( IServiceCollection services )
    {
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOpenSearchClient>();
    }

    // ---- the hook reaches the client -------------------------------------

    [TestMethod]
    public void AddOpenSearchClient_ConfigureSettings_AppliesToResolvedClient()
    {
        var services = new ServiceCollection();
        services.AddOpenSearchClient( Endpoint, configureSettings: settings =>
            settings.RequestTimeout( TimeSpan.FromSeconds( 97 ) ) );

        Resolve( services ).ConnectionSettings.RequestTimeout
            .Should().Be( TimeSpan.FromSeconds( 97 ) );
    }

    [TestMethod]
    public void AddOpenSearchClient_ConfigureSettings_IsOptional()
    {
        // Existing call shapes keep working unchanged — the parameter is optional
        // and every overload is source-compatible.
        var services = new ServiceCollection();
        services.AddOpenSearchClient( Endpoint );

        Resolve( services ).Should().NotBeNull();
    }

    [TestMethod]
    public void AddOpenSearchClient_ConfigureSettings_RunsAfterAuthWiring()
    {
        // The ordering guarantee: the hook can override what the library set.
        // Basic auth writes a global header; the hook replaces it, and the
        // replacement is what survives.
        var services = new ServiceCollection();
        services.AddOpenSearchClient(
            Endpoint,
            auth =>
            {
                auth.Mode = OpenSearchAuthenticationMode.Basic;
                auth.UserName = "library";
                auth.Password = "set-first";
            },
            settings => settings.BasicAuthentication( "consumer", "wins" ) );

        var credentials = Resolve( services ).ConnectionSettings.BasicAuthenticationCredentials;

        credentials.Should().NotBeNull();
        credentials!.Username.Should().Be( "consumer" );
    }

    [TestMethod]
    public void AddOpenSearchClient_FromConfiguration_ForwardsConfigureSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection( new Dictionary<string, string?>
            {
                ["OpenSearch:ConnectionString"] = Endpoint.ToString()
            } )
            .Build();

        var services = new ServiceCollection();
        services.AddOpenSearchClient( configuration, settings =>
            settings.MaximumRetries( 7 ) );

        Resolve( services ).ConnectionSettings.MaxRetries.Should().Be( 7 );
    }

    [TestMethod]
    public void AddOpenSearchAwsClient_ConfigureSettings_AppliesToResolvedClient()
    {
        var services = new ServiceCollection();
        services.AddOpenSearchAwsClient(
            AwsEndpoint,
            aws =>
            {
                aws.Region = "us-east-1";
                aws.Credentials = new Amazon.Runtime.BasicAWSCredentials( "ak", "sk" );
            },
            settings => settings.RequestTimeout( TimeSpan.FromSeconds( 43 ) ) );

        Resolve( services ).ConnectionSettings.RequestTimeout
            .Should().Be( TimeSpan.FromSeconds( 43 ) );
    }

    // ---- the hook cannot silently break the ledger -----------------------

    [TestMethod]
    public void AddOpenSearchClient_ConfigureSettingsBreakingFieldInference_ThrowsWithRemediation()
    {
        // The ledger index is created with a strict mapping using camelCase field
        // names. A client-wide DefaultFieldNameInferrer that is not camelCase makes
        // every ledger write fail with strict_dynamic_mapping_exception at first
        // write — loud, but it names fields rather than the cause and reads like a
        // schema problem. Fail at registration, where it was introduced.
        var services = new ServiceCollection();
        services.AddOpenSearchClient( Endpoint, configureSettings: settings =>
            settings.DefaultFieldNameInferrer( name => name ) );

        var act = () => Resolve( services );

        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "appliedBy" )
                          && ex.Message.Contains( "strict" )
                          && ex.Message.Contains( "DefaultMappingFor" ) );
    }

    [TestMethod]
    public void AddOpenSearchAwsClient_ConfigureSettingsBreakingFieldInference_ThrowsWithRemediation()
    {
        // Same validation on the SigV4 path — both factories share one builder.
        var services = new ServiceCollection();
        services.AddOpenSearchAwsClient(
            AwsEndpoint,
            aws =>
            {
                aws.Region = "us-east-1";
                aws.Credentials = new Amazon.Runtime.BasicAWSCredentials( "ak", "sk" );
            },
            settings => settings.DefaultFieldNameInferrer( name => name.ToUpperInvariant() ) );

        var act = () => Resolve( services );

        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "appliedBy" ) );
    }

    [TestMethod]
    public void AddOpenSearchClient_ConfigureSettingsScopedToConsumerTypes_IsAllowed()
    {
        // The remediation the error message recommends must actually work: a
        // DefaultMappingFor over the consumer's OWN type does not touch the
        // ledger's field naming, so it passes.
        var services = new ServiceCollection();
        services.AddOpenSearchClient( Endpoint, configureSettings: settings =>
            settings.DefaultMappingFor<ConsumerDocument>( m => m
                .IndexName( "consumer-documents" ) ) );

        var client = Resolve( services );

        client.ConnectionSettings.Inferrer.IndexName( typeof( ConsumerDocument ) )
            .Should().Be( "consumer-documents" );
    }

    [TestMethod]
    public void AddOpenSearchClient_ConsumerDefaultIndex_DoesNotChangeLedgerBehavior()
    {
        // ADR-0029 cross-check: the ledger must not read consumer index inference
        // even when the consumer has now made it resolvable. A DefaultIndex is a
        // legitimate thing to set for application code and must not silently
        // become the ledger's index.
        var services = new ServiceCollection();
        services.AddOpenSearchClient( Endpoint, configureSettings: settings =>
            settings.DefaultIndex( "consumer-default" ) );

        var client = Resolve( services );

        client.ConnectionSettings.DefaultIndex.Should().Be( "consumer-default" );

        // The ledger still targets OpenSearchMigrationOptions.LedgerIndex.
        new OpenSearchMigrationOptions().LedgerIndex
            .Should().NotBe( "consumer-default" );
    }

    // ---- binary compatibility with 3.1.x --------------------------------

    [TestMethod]
    public void ClientFactories_Keep_The_3_1_x_Signatures_Intact()
    {
        // configureSettings ships as a separate overload rather than an appended
        // optional parameter, because appending one changes the existing method's
        // signature: the 3.1.x entry point stops existing and any assembly compiled
        // against it throws MissingMethodException until recompiled. This library
        // claims SemVer, and a minor release may not do that.
        //
        // Reflection, not a call site -- a source-level call would bind happily to a
        // widened signature and prove nothing. These assert the exact parameter lists
        // a 3.1.x-compiled caller resolved against still exist.
        AssertOverload( typeof( CoreExtensions ), "AddOpenSearchClient",
            typeof( IServiceCollection ), typeof( Uri ), typeof( Action<OpenSearchAuthenticationOptions> ) );

        AssertOverload( typeof( CoreExtensions ), "AddOpenSearchClient",
            typeof( IServiceCollection ), typeof( IConfiguration ) );

        AssertOverload( typeof( AwsExtensions ), "AddOpenSearchAwsClient",
            typeof( IServiceCollection ), typeof( Uri ), typeof( Action<OpenSearchAwsAuthenticationOptions> ) );

        AssertOverload( typeof( AwsExtensions ), "AddOpenSearchAwsClient",
            typeof( IServiceCollection ), typeof( IConfiguration ) );
    }

    [TestMethod]
    public void ClientFactories_Add_The_ConfigureSettings_Overloads()
    {
        AssertOverload( typeof( CoreExtensions ), "AddOpenSearchClient",
            typeof( IServiceCollection ), typeof( Uri ), typeof( Action<OpenSearchAuthenticationOptions> ), typeof( Action<ConnectionSettings> ) );

        AssertOverload( typeof( CoreExtensions ), "AddOpenSearchClient",
            typeof( IServiceCollection ), typeof( IConfiguration ), typeof( Action<ConnectionSettings> ) );

        AssertOverload( typeof( AwsExtensions ), "AddOpenSearchAwsClient",
            typeof( IServiceCollection ), typeof( Uri ), typeof( Action<OpenSearchAwsAuthenticationOptions> ), typeof( Action<ConnectionSettings> ) );

        AssertOverload( typeof( AwsExtensions ), "AddOpenSearchAwsClient",
            typeof( IServiceCollection ), typeof( IConfiguration ), typeof( Action<ConnectionSettings> ) );
    }

    private static void AssertOverload( Type declaring, string name, params Type[] parameterTypes )
    {
        var found = declaring
            .GetMethods( BindingFlags.Public | BindingFlags.Static )
            .Where( m => m.Name == name )
            .Any( m => m.GetParameters().Select( p => p.ParameterType ).SequenceEqual( parameterTypes ) );

        found.Should().BeTrue(
            "{0}.{1}({2}) must exist",
            declaring.Name, name, string.Join( ", ", parameterTypes.Select( t => t.Name ) ) );
    }

    private sealed class ConsumerDocument
    {
        public string? Id { get; init; }
    }
}
