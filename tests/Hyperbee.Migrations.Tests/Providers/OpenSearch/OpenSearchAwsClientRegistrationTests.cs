#nullable enable
using Amazon.Runtime;
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Aws;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// R-21 — option-E registration semantics. Two extensions, mutually exclusive.
//
// Core's AddOpenSearchClient handles header-based auth (Basic, ApiKey, mTLS,
// Anonymous) and rejects AWS endpoints with a remediation message naming
// AddOpenSearchAwsClient.
//
// AddOpenSearchAwsClient (in the .Aws extension package) handles SigV4
// transport replacement and rejects subsequent client registrations with a
// remediation message naming the alternative.
//
// These tests pin the registration-time semantics — live HTTP and signing
// behavior live in integration tests against AWS Managed (a separate
// scheduled run per R-28c).

[TestClass]
public class OpenSearchAwsClientRegistrationTests
{
    // ---- AWS-endpoint URL guard in core ----

    [TestMethod]
    public void AddOpenSearchClient_AwsManagedEndpoint_Throws_WithRemediation()
    {
        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchClient(
            new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ),
            opts => opts.Mode = OpenSearchAuthenticationMode.Anonymous );

        act.Should().Throw<AwsSigV4NotConfiguredException>()
            .Where( ex => ex.Message.Contains( "amazonaws.com" )
                          && ex.Message.Contains( "AddOpenSearchAwsClient" )
                          && ex.Message.Contains( "Hyperbee.Migrations.Providers.OpenSearch.Aws" ) );
    }

    [TestMethod]
    public void AddOpenSearchClient_OpenSearchServerlessEndpoint_AlsoThrows()
    {
        // OpenSearch Serverless: <id>.<region>.aoss.amazonaws.com
        // Same .amazonaws.com suffix, same loud-fail.
        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchClient(
            new Uri( "https://abc123.us-east-1.aoss.amazonaws.com" ),
            opts => opts.Mode = OpenSearchAuthenticationMode.Anonymous );

        act.Should().Throw<AwsSigV4NotConfiguredException>();
    }

    [TestMethod]
    public void AddOpenSearchClient_NonAwsEndpoint_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchClient(
            new Uri( "http://localhost:9200" ),
            opts => opts.Mode = OpenSearchAuthenticationMode.Anonymous );

        act.Should().NotThrow();
    }

    [TestMethod]
    public void AddOpenSearchClient_HostnameContainsAmazonaws_NotASuffixMatch()
    {
        // Substring "amazonaws.com" should NOT match in a hostname like
        // "amazonaws.com.attacker.test" — the check uses EndsWith so this
        // resolves to a non-AWS endpoint correctly.
        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchClient(
            new Uri( "https://amazonaws.com.attacker.test" ),
            opts => opts.Mode = OpenSearchAuthenticationMode.Anonymous );

        act.Should().NotThrow();
    }

    [TestMethod]
    public void AddOpenSearchClient_AmazonawsHost_CaseInsensitive()
    {
        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchClient(
            new Uri( "https://my-domain.us-east-1.es.AMAZONAWS.com" ),
            opts => opts.Mode = OpenSearchAuthenticationMode.Anonymous );

        act.Should().Throw<AwsSigV4NotConfiguredException>();
    }

    // ---- Mutual exclusion ----

    [TestMethod]
    public void AddOpenSearchClient_AfterAwsClient_Throws()
    {
        var services = new ServiceCollection();
        services.AddOpenSearchAwsClient(
            new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ),
            opts => opts.Region = "us-east-1" );

        var act = () => services.AddOpenSearchClient(
            new Uri( "http://localhost:9200" ),
            opts => opts.Mode = OpenSearchAuthenticationMode.Anonymous );

        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "mutually exclusive" )
                          || ex.Message.Contains( "exactly one" ) );
    }

    [TestMethod]
    public void AddOpenSearchAwsClient_AfterCoreClient_Throws()
    {
        var services = new ServiceCollection();
        services.AddOpenSearchClient(
            new Uri( "http://localhost:9200" ),
            opts => opts.Mode = OpenSearchAuthenticationMode.Anonymous );

        var act = () => services.AddOpenSearchAwsClient(
            new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ),
            opts => opts.Region = "us-east-1" );

        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "mutually exclusive" )
                          || ex.Message.Contains( "exactly one" ) );
    }

    // ---- AWS auth options validation ----

    [TestMethod]
    public void AddOpenSearchAwsClient_MissingRegion_Throws()
    {
        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchAwsClient(
            new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ),
            opts => { /* deliberately missing Region */ } );

        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "Region" ) );
    }

    [TestMethod]
    public void AddOpenSearchAwsClient_UnknownRegion_Throws_AtRegistrationTime()
    {
        // R-21: typos in region should fail at registration time, not at
        // first wire request. Validates against AWSSDK's known-region list.
        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchAwsClient(
            new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ),
            opts => opts.Region = "us-east1" );  // missing dash

        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "us-east1" ) || ex.Message.Contains( "not a recognized" ) );
    }

    [TestMethod]
    public void AddOpenSearchAwsClient_ValidConfig_RegistersClient()
    {
        var services = new ServiceCollection();
        services.AddOpenSearchAwsClient(
            new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ),
            opts =>
            {
                opts.Region = "us-east-1";
                opts.Service = "es";
                // Use BasicAWSCredentials so the singleton resolution doesn't
                // fall back to the ambient AWS chain (which may or may not
                // be present in test environments).
                opts.Credentials = new BasicAWSCredentials( "AKIA-test", "secret-test" );
            } );

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IOpenSearchClient>();
        client.Should().NotBeNull();
    }

    [TestMethod]
    public void AddOpenSearchAwsClient_ServiceDefaultsToEs()
    {
        var opts = new OpenSearchAwsAuthenticationOptions();
        opts.Service.Should().Be( "es", because: "default service code is `es` for OpenSearch Service domains" );
    }

    // ---- IConfiguration overload ----

    [TestMethod]
    public void AddOpenSearchAwsClient_FromConfiguration_ReadsRegionAndService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection( new Dictionary<string, string?>
            {
                ["OpenSearch:ConnectionString"] = "https://my-domain.us-east-1.es.amazonaws.com",
                ["OpenSearch:Authentication:Region"] = "us-east-1",
                ["OpenSearch:Authentication:Service"] = "aoss"
            } )
            .Build();

        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchAwsClient( config );
        act.Should().NotThrow();
    }

    [TestMethod]
    public void AddOpenSearchAwsClient_FromConfiguration_MissingConnectionString_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection( new Dictionary<string, string?>
            {
                ["OpenSearch:Authentication:Region"] = "us-east-1"
            } )
            .Build();

        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchAwsClient( config );
        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "ConnectionString" ) );
    }
}
