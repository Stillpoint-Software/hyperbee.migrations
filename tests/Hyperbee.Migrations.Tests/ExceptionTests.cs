
using System;
using Hyperbee.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ExceptionTests
{
    [TestMethod]
    public void MigrationException_Constructors_Work()
    {
        var ex1 = new MigrationException();
        Assert.AreEqual( "Migration exception", ex1.Message );

        var ex2 = new MigrationException( "msg" );
        Assert.AreEqual( "msg", ex2.Message );

        var inner = new Exception( "inner" );
        var ex3 = new MigrationException( "msg2", inner );
        Assert.AreEqual( "msg2", ex3.Message );
        Assert.AreEqual( inner, ex3.InnerException );
    }

    [TestMethod]
    public void MigrationLockUnavailableException_Constructors_Work()
    {
        var ex1 = new MigrationLockUnavailableException();
        Assert.AreEqual( "Migration lock unavailable exception", ex1.Message );

        var ex2 = new MigrationLockUnavailableException( "msg" );
        Assert.AreEqual( "msg", ex2.Message );

        var inner = new Exception( "inner" );
        var ex3 = new MigrationLockUnavailableException( "msg2", inner );
        Assert.AreEqual( "msg2", ex3.Message );
        Assert.AreEqual( inner, ex3.InnerException );
    }

    [TestMethod]
    public void MigrationTimeoutException_Constructors_Work()
    {
        var ex1 = new MigrationTimeoutException();
        Assert.AreEqual( "Migration timeout exception", ex1.Message );

        var ex2 = new MigrationTimeoutException( "msg" );
        Assert.AreEqual( "msg", ex2.Message );

        var inner = new Exception( "inner" );
        var ex3 = new MigrationTimeoutException( "msg2", inner );
        Assert.AreEqual( "msg2", ex3.Message );
        Assert.AreEqual( inner, ex3.InnerException );
    }

    [TestMethod]
    public void DuplicateMigrationException_Constructors_Work()
    {
        var ex1 = new DuplicateMigrationException();
        Assert.AreEqual( "Duplicate migration exception", ex1.Message );

        var ex2 = new DuplicateMigrationException( "msg" );
        Assert.AreEqual( "msg", ex2.Message );

        var inner = new Exception( "inner" );
        var ex3 = new DuplicateMigrationException( "msg2", inner );
        Assert.AreEqual( "msg2", ex3.Message );
        Assert.AreEqual( inner, ex3.InnerException );

        var ex4 = new DuplicateMigrationException( "msg3", 42 );
        Assert.AreEqual( 42, ex4.Id );
        Assert.AreEqual( "msg3", ex4.Message );

        var ex5 = new DuplicateMigrationException( "msg4", 99, inner );
        Assert.AreEqual( 99, ex5.Id );
        Assert.AreEqual( "msg4", ex5.Message );
        Assert.AreEqual( inner, ex5.InnerException );
    }
}
