using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.SquashCli;
using Hyperbee.Migrations.Providers.Couchbase.SquashCli;
using Hyperbee.Migrations.Providers.MongoDB.SquashCli;
using Hyperbee.Migrations.Providers.OpenSearch.SquashCli;
using Hyperbee.Migrations.Providers.Postgres.SquashCli;
using Hyperbee.Migrations.Squash.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Pin the ISquashCliProvider contract surface for each shipped provider:
// ProviderId stable, file extension stable, scanner returns empty on
// missing-path (defensive default). Heavy end-to-end coverage (ephemeral
// container -> apply migrations -> capture snapshot) ships in the
// INTEGRATIONS-gated suites that already exercise the provider's strategy
// pipeline; this is a thin contract pin.

[TestClass]
public class SquashCliProviderContractTests
{
    [TestMethod]
    public void PostgresProvider_ContractValues()
    {
        var provider = new PostgresSquashCliProvider();
        provider.ProviderId.Should().Be( "postgres" );
        provider.SquashFileExtension.Should().Be( ".sql" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void AerospikeProvider_ContractValues()
    {
        var provider = new AerospikeSquashCliProvider();
        provider.ProviderId.Should().Be( "aerospike" );
        provider.SquashFileExtension.Should().Be( ".statements" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void OpenSearchProvider_ContractValues()
    {
        var provider = new OpenSearchSquashCliProvider();
        provider.ProviderId.Should().Be( "opensearch" );
        provider.SquashFileExtension.Should().Be( ".statements" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void MongoDBProvider_ContractValues()
    {
        var provider = new MongoDBSquashCliProvider();
        provider.ProviderId.Should().Be( "mongodb" );
        provider.SquashFileExtension.Should().Be( ".statements" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void CouchbaseProvider_ContractValues()
    {
        var provider = new CouchbaseSquashCliProvider();
        provider.ProviderId.Should().Be( "couchbase" );
        provider.SquashFileExtension.Should().Be( ".statements" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }
}
