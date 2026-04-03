using Hyperbee.Migrations.Providers.Aerospike;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class AerospikeOptionsTests
{
    [TestMethod]
    public void Should_have_correct_defaults()
    {
        var options = new AerospikeMigrationOptions();

        Assert.AreEqual( "test", options.Namespace );
        Assert.AreEqual( "SchemaMigrations", options.MigrationSet );
        Assert.AreEqual( "migration_lock", options.LockName );
        Assert.AreEqual( TimeSpan.FromHours( 1 ), options.LockMaxLifetime );
    }

    [TestMethod]
    public void Should_deconstruct()
    {
        var options = new AerospikeMigrationOptions
        {
            Namespace = "myns",
            MigrationSet = "myset",
            LockName = "mylock"
        };

        var (@namespace, migrationSet, lockName) = options;

        Assert.AreEqual( "myns", @namespace );
        Assert.AreEqual( "myset", migrationSet );
        Assert.AreEqual( "mylock", lockName );
    }

    [TestMethod]
    public void Should_allow_custom_values()
    {
        var options = new AerospikeMigrationOptions
        {
            Namespace = "production",
            MigrationSet = "Migrations",
            LockName = "deploy_lock",
            LockMaxLifetime = TimeSpan.FromMinutes( 30 )
        };

        Assert.AreEqual( "production", options.Namespace );
        Assert.AreEqual( "Migrations", options.MigrationSet );
        Assert.AreEqual( "deploy_lock", options.LockName );
        Assert.AreEqual( TimeSpan.FromMinutes( 30 ), options.LockMaxLifetime );
    }
}
