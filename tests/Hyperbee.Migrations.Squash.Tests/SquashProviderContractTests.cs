using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Pin the ISquashProvider contract surface for each shipped provider:
// ProviderId stable, file extension stable, scanner returns empty on
// missing-path (defensive default). Heavy end-to-end coverage (ephemeral
// container -> apply migrations -> capture snapshot) ships in the
// INTEGRATIONS-gated suites that already exercise the provider's strategy
// pipeline; this is a thin contract pin.

[TestClass]
public class SquashProviderContractTests
{
    [TestMethod]
    public void PostgresProvider_ContractValues()
    {
        var provider = new PostgresSquashProvider();
        provider.ProviderId.Should().Be( "postgres" );
        provider.SquashFileExtension.Should().Be( ".sql" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void AerospikeProvider_ContractValues()
    {
        var provider = new AerospikeSquashProvider();
        provider.ProviderId.Should().Be( "aerospike" );
        provider.SquashFileExtension.Should().Be( ".pql" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void OpenSearchProvider_ContractValues()
    {
        var provider = new OpenSearchSquashProvider();
        provider.ProviderId.Should().Be( "opensearch" );
        provider.SquashFileExtension.Should().Be( ".pql" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void MongoDBProvider_ContractValues()
    {
        var provider = new MongoDBSquashProvider();
        provider.ProviderId.Should().Be( "mongodb" );
        provider.SquashFileExtension.Should().Be( ".pql" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }

    [TestMethod]
    public void CouchbaseProvider_ContractValues()
    {
        var provider = new CouchbaseSquashProvider();
        provider.ProviderId.Should().Be( "couchbase" );
        provider.SquashFileExtension.Should().Be( ".pql" );
        provider.ScanSource( null! ).Should().BeEmpty();
        provider.ScanSource( "" ).Should().BeEmpty();
    }
}
