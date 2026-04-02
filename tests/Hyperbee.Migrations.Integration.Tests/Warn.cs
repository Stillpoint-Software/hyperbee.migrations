namespace Hyperbee.Migrations.Integration.Tests;

public static class Warn
{
    public static void If( bool condition, string message )
    {
        if ( condition )
            Assert.Inconclusive( message );
    }
}
