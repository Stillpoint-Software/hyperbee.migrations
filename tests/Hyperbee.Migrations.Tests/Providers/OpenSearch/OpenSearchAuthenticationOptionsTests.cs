#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// R-21 — auth mode validation. Live-cluster auth handshakes are exercised
// by integration tests; the unit tests here cover the validation contract:
// each mode names its required fields and fails with a remediation message
// when they're missing.

[TestClass]
public class OpenSearchAuthenticationOptionsTests
{
    [TestMethod]
    public void Anonymous_NoFields_PassesValidation()
    {
        var opts = new OpenSearchAuthenticationOptions { Mode = OpenSearchAuthenticationMode.Anonymous };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Basic_RequiresUserName()
    {
        var opts = new OpenSearchAuthenticationOptions { Mode = OpenSearchAuthenticationMode.Basic };
        var act = () => opts.Validate();
        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*Basic*UserName*" );
    }

    [TestMethod]
    public void Basic_AllowsEmptyPassword()
    {
        // Test fixtures (e.g., disabled-security single-node) often run with
        // an empty password. Validation should not require it.
        var opts = new OpenSearchAuthenticationOptions
        {
            Mode = OpenSearchAuthenticationMode.Basic,
            UserName = "admin"
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [TestMethod]
    public void ApiKey_RequiresApiKeyId()
    {
        var opts = new OpenSearchAuthenticationOptions
        {
            Mode = OpenSearchAuthenticationMode.ApiKey,
            ApiKey = "the-secret"
        };
        var act = () => opts.Validate();
        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*ApiKey*ApiKeyId*" );
    }

    [TestMethod]
    public void ApiKey_RequiresApiKey()
    {
        var opts = new OpenSearchAuthenticationOptions
        {
            Mode = OpenSearchAuthenticationMode.ApiKey,
            ApiKeyId = "the-id"
        };
        var act = () => opts.Validate();
        act.Should().Throw<OpenSearchProviderException>()
            .Where( ex => ex.Message.Contains( "ApiKey" ) && ex.Message.Contains( "user-secrets" ) );
    }

    [TestMethod]
    public void ApiKey_BothFields_PassesValidation()
    {
        var opts = new OpenSearchAuthenticationOptions
        {
            Mode = OpenSearchAuthenticationMode.ApiKey,
            ApiKeyId = "id",
            ApiKey = "key"
        };
        opts.Validate();   // does not throw
    }

    [TestMethod]
    public void ClientCertificate_RequiresEitherPathOrInstance()
    {
        var opts = new OpenSearchAuthenticationOptions
        {
            Mode = OpenSearchAuthenticationMode.ClientCertificate
        };
        var act = () => opts.Validate();
        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*ClientCertificate*ClientCertificatePath*" );
    }

    [TestMethod]
    public void ClientCertificate_PathThatDoesNotExist_Fails()
    {
        var opts = new OpenSearchAuthenticationOptions
        {
            Mode = OpenSearchAuthenticationMode.ClientCertificate,
            ClientCertificatePath = Path.Combine( Path.GetTempPath(), $"never-existed-{Guid.NewGuid():N}.pfx" )
        };
        var act = () => opts.Validate();
        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*does not exist*" );
    }

    [TestMethod]
    public void ClientCertificate_BothPathAndInstance_FailsAsMutuallyExclusive()
    {
        // Build a throwaway self-signed cert in memory so we can test the
        // mutual-exclusion guard without needing a real PFX file.
        using var rsa = System.Security.Cryptography.RSA.Create( 2048 );
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=test",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1 );
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes( -1 ),
            DateTimeOffset.UtcNow.AddMinutes( 5 ) );

        var opts = new OpenSearchAuthenticationOptions
        {
            Mode = OpenSearchAuthenticationMode.ClientCertificate,
            ClientCertificate = cert,
            ClientCertificatePath = Path.GetTempFileName() // fake path
        };

        try
        {
            var act = () => opts.Validate();
            act.Should().Throw<OpenSearchProviderException>()
                .Where( ex => ex.Message.Contains( "BOTH" ) || ex.Message.Contains( "exactly one" ) );
        }
        finally
        {
            File.Delete( opts.ClientCertificatePath! );
        }
    }

    [TestMethod]
    public void AddOpenSearchClient_AnonymousMode_RegistersClient()
    {
        // Smoke: registration succeeds for the default mode and the IOpenSearchClient
        // resolves. Live HTTP isn't exercised here; that's an integration concern.
        var services = new ServiceCollection();
        services.AddOpenSearchClient( new Uri( "http://localhost:9200" ), opts =>
        {
            opts.Mode = OpenSearchAuthenticationMode.Anonymous;
        } );

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IOpenSearchClient>();
        client.Should().NotBeNull();
    }

    [TestMethod]
    public void AddOpenSearchClient_FromConfiguration_LegacyFlatUserPassword_TreatedAsBasic()
    {
        // Back-compat: callers pre-Slice-3.4 may have config like
        // OpenSearch:UserName / OpenSearch:Password without a Mode key.
        // The provider should treat that as Basic.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection( new Dictionary<string, string?>
            {
                ["OpenSearch:ConnectionString"] = "http://localhost:9200",
                ["OpenSearch:UserName"] = "legacy-user",
                ["OpenSearch:Password"] = "legacy-pwd"
            } )
            .Build();

        var services = new ServiceCollection();
        services.AddOpenSearchClient( config );

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IOpenSearchClient>();
        client.Should().NotBeNull();
    }

    [TestMethod]
    public void AddOpenSearchClient_FromConfiguration_UnknownMode_ThrowsRemediation()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection( new Dictionary<string, string?>
            {
                ["OpenSearch:ConnectionString"] = "http://localhost:9200",
                ["OpenSearch:Authentication:Mode"] = "Quantum"
            } )
            .Build();

        var services = new ServiceCollection();
        var act = () => services.AddOpenSearchClient( config );
        act.Should().Throw<OpenSearchProviderException>()
            .WithMessage( "*Quantum*Anonymous, Basic, ApiKey, ClientCertificate*" );
    }

    [TestMethod]
    public void AddOpenSearchClient_FromConfiguration_CaseInsensitiveModeParsing()
    {
        // Config file authors may write 'apikey' or 'ApiKey' or 'APIKEY' —
        // all should resolve to the same mode.
        foreach ( var modeStr in new[] { "ApiKey", "apikey", "APIKEY" } )
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection( new Dictionary<string, string?>
                {
                    ["OpenSearch:ConnectionString"] = "http://localhost:9200",
                    ["OpenSearch:Authentication:Mode"] = modeStr,
                    ["OpenSearch:Authentication:ApiKeyId"] = "id",
                    ["OpenSearch:Authentication:ApiKey"] = "key"
                } )
                .Build();

            var services = new ServiceCollection();
            var act = () => services.AddOpenSearchClient( config );
            act.Should().NotThrow( $"`{modeStr}` should parse as ApiKey" );
        }
    }

    [TestMethod]
    public void Validate_UnknownMode_Throws()
    {
        var opts = new OpenSearchAuthenticationOptions { Mode = (OpenSearchAuthenticationMode) 99 };
        var act = () => opts.Validate();
        act.Should().Throw<OpenSearchProviderException>();
    }
}
