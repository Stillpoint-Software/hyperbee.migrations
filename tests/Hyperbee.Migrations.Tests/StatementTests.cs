using Hyperbee.Migrations.Providers.Couchbase;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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


    public static IndexItem GetIndexItem( string statement )
    {
        // hackish method to parse out the bucket and index name from an index statement.
        // the regex could do with improvement. trimming, different kinds (or lack of)
        // whitespace, leading and trailing whitespace, \r\n, etc.

        var splitChars = new[] { '\'', '`', ' ', '\t', '(' };

        // CREATE [PRIMARY] INDEX <index> ON <bucket> [..rest] | BUILD INDEX ON <bucket> [..rest]

        var match = Regex.Match( statement, @"^\s*(?:CREATE|BUILD)\s+(?<opt>PRIMARY\s+)?INDEX\s*(?<idx>.*)\s+ON\s*?(?<on>[^\s]+)", RegexOptions.IgnoreCase );

        var isPrimary = match.Groups["opt"].Value
            .StartsWith( "PRIMARY", StringComparison.OrdinalIgnoreCase );

        var indexName = match.Groups["idx"].Value
            .Split( splitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .FirstOrDefault()
            ?.Trim( splitChars );

        var bucketName = match.Groups["on"].Value
            .Split( splitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .FirstOrDefault()
            ?.Trim( splitChars );

        return new IndexItem( bucketName, indexName, statement, isPrimary );
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