using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Hyperbee.Migrations.Providers.Couchbase.Parsers;

// ReSharper disable UnusedMember.Global
// ReSharper disable InconsistentNaming

namespace Hyperbee.Migrations.Tests;

[TestClass]
public class StatementTests
{
    [TestMethod]
    public void Evaluate_statements()
    {
        var statements = GetStatements( "Hyperbee.Migrations.Tests.Resources.statements.json" );

        var parser = new StatementParser();

        var results = statements.Select( parser.ParseStatement ).ToList();
    }

    public static IEnumerable<string> GetStatements( string resourceName )
    {
        var json = GetResource( resourceName );
        var node = JsonNode.Parse( json );

        return node!["statements"]!.AsArray()
            .Select( e => e["statement"]?.ToString() )
            .Where( x => x != null );
    }

    public static string GetResource( string fullyQualifiedName )
    {
        using var stream = typeof(StatementTests).Assembly.GetManifestResourceStream( fullyQualifiedName );

        if ( stream == null )
            throw new FileNotFoundException( $"Cannot find '{fullyQualifiedName}'." );

        using var reader = new StreamReader( stream );
        return reader.ReadToEnd();
    }
}