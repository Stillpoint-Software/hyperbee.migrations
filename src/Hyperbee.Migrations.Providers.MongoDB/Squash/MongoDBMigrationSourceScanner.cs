using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// Roslyn-based scanner that walks a directory of migration source files and
/// classifies each <see cref="Migration"/>-derived class against the
/// <c>[DataMigration]</c> / <c>[StructuralOnly]</c> annotation requirement
/// (per ADR-0019 amendment A5).
/// </summary>
/// <remarks>
/// <para>
/// MongoDB migrations write data via the C# driver (<c>collection.InsertOneAsync</c>,
/// <c>BulkWriteAsync</c>, <c>UpdateManyAsync</c>, etc.). The scanner detects
/// these invocations and classifies any unannotated class containing them
/// as requiring annotation.
/// </para>
/// <para>
/// V1 detection scope:
/// <list type="bullet">
///   <item>Write call sites by method name: <c>Insert*</c>, <c>Update*</c>,
///         <c>UpdateByQuery*</c>, <c>Delete*</c>, <c>DeleteByQuery*</c>,
///         <c>Replace*</c>, <c>BulkWrite*</c>, <c>FindOneAndUpdate*</c>,
///         <c>FindOneAndReplace*</c>, <c>FindOneAndDelete*</c>.</item>
///   <item>Non-determinism call sites (same catalog as Aerospike +
///         OpenSearch): <c>DateTime.Now/UtcNow/Today</c>,
///         <c>DateTimeOffset.Now/UtcNow</c>, <c>Guid.NewGuid</c>,
///         <c>Random.Shared</c>, <c>Environment.MachineName/UserName/ProcessId</c>,
///         <c>Stopwatch.GetTimestamp</c>, <c>new Random()</c> without seed.</item>
/// </list>
/// </para>
/// <para>
/// <b>Receiver-anchoring trade-off (same as the data-op classifier):</b>
/// unlike Aerospike + OpenSearch (whose scanners anchor on <c>_client.*</c>
/// receivers), MongoDB code routes through local <c>collection</c> /
/// <c>db</c> variables. The scanner therefore matches method names without
/// receiver-name filtering. False positives possible: a user class with its
/// own method named <c>InsertOne</c> may be flagged. Mitigation: the operator
/// annotates with <c>[StructuralOnly]</c> to suppress. False positives are
/// safer than false negatives under the default-deny posture.
/// </para>
/// <para>
/// <b>Cross-provider hoist candidate (Phase 5):</b> the Migration-extends
/// check, the attribute recognizer, and the non-determinism scan are now
/// identical across FOUR providers (Postgres, Aerospike, OpenSearch, and
/// this MongoDB scanner). The receiver-anchoring portion differs per
/// provider; the rest is sharable.
/// </para>
/// </remarks>
public static class MongoDBMigrationSourceScanner
{
    public sealed record ClassVerdict(
        string ClassName,
        string FilePath,
        bool ExtendsMigration,
        bool HasMigrationAttribute,
        bool HasDataMigrationAttribute,
        bool HasStructuralOnlyAttribute,
        bool LooksLikeDataOp,
        IReadOnlyList<string> NonDeterminismHits,
        IReadOnlyList<string> DataOpHits )
    {
        public bool RequiresAnnotation =>
            ExtendsMigration
            && HasMigrationAttribute
            && (LooksLikeDataOp || NonDeterminismHits.Count > 0)
            && !HasDataMigrationAttribute
            && !HasStructuralOnlyAttribute;
    }

    // MongoDB data-op method names. Matched by name regardless of receiver
    // (per the receiver-anchoring trade-off above).
    private static readonly HashSet<string> WriteMethods = new( StringComparer.Ordinal )
    {
        "InsertOne", "InsertOneAsync", "InsertMany", "InsertManyAsync",
        "UpdateOne", "UpdateOneAsync", "UpdateMany", "UpdateManyAsync",
        "ReplaceOne", "ReplaceOneAsync",
        "DeleteOne", "DeleteOneAsync", "DeleteMany", "DeleteManyAsync",
        "FindOneAndUpdate", "FindOneAndUpdateAsync",
        "FindOneAndReplace", "FindOneAndReplaceAsync",
        "FindOneAndDelete", "FindOneAndDeleteAsync",
        "BulkWrite", "BulkWriteAsync"
    };

    private static readonly HashSet<string> NonDeterminismMembers = new( StringComparer.Ordinal )
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTime.Today",
        "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "Guid.NewGuid",
        "Environment.MachineName", "Environment.UserName", "Environment.ProcessId",
        "Stopwatch.GetTimestamp", "Activity.Current",
        "Random.Shared"
    };

    public static IReadOnlyList<ClassVerdict> Scan( string sourceRoot )
    {
        if ( string.IsNullOrWhiteSpace( sourceRoot ) )
            throw new ArgumentException( "sourceRoot is required.", nameof( sourceRoot ) );
        if ( !Directory.Exists( sourceRoot ) )
            throw new DirectoryNotFoundException( $"sourceRoot `{sourceRoot}` does not exist." );

        var verdicts = new List<ClassVerdict>();

        foreach ( var file in Directory.EnumerateFiles( sourceRoot, "*.cs", SearchOption.AllDirectories ) )
        {
            var rel = Path.GetRelativePath( sourceRoot, file ).Replace( '\\', '/' );
            if ( rel.StartsWith( "obj/", StringComparison.OrdinalIgnoreCase )
                 || rel.StartsWith( "bin/", StringComparison.OrdinalIgnoreCase )
                 || rel.Contains( "/obj/", StringComparison.OrdinalIgnoreCase )
                 || rel.Contains( "/bin/", StringComparison.OrdinalIgnoreCase ) )
            {
                continue;
            }

            var source = File.ReadAllText( file );
            var tree = CSharpSyntaxTree.ParseText( source );
            var root = tree.GetCompilationUnitRoot();

            foreach ( var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>() )
                verdicts.Add( ClassifyClass( classDecl, file ) );
        }

        return verdicts;
    }

    public static IReadOnlyList<ClassVerdict> ScanSource( string sourceText, string filePath = "<inline>" )
    {
        if ( sourceText == null )
            throw new ArgumentNullException( nameof( sourceText ) );

        var tree = CSharpSyntaxTree.ParseText( sourceText );
        var root = tree.GetCompilationUnitRoot();

        var verdicts = new List<ClassVerdict>();
        foreach ( var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>() )
            verdicts.Add( ClassifyClass( classDecl, filePath ) );

        return verdicts;
    }

    private static ClassVerdict ClassifyClass( ClassDeclarationSyntax classDecl, string filePath )
    {
        var className = classDecl.Identifier.ValueText;
        var extendsMigration = ClassExtendsMigration( classDecl );
        var attrs = ClassAttributes( classDecl );

        var hasMigrationAttr = attrs.Any( a => IsAttributeName( a, "Migration" ) );
        var hasDataMigrationAttr = attrs.Any( a => IsAttributeName( a, "DataMigration" ) );
        var hasStructuralOnlyAttr = attrs.Any( a => IsAttributeName( a, "StructuralOnly" ) );

        // Walk method-body invocation expressions; collect write-method hits.
        // Per the receiver-anchoring trade-off documented in the class
        // docstring, match by method name regardless of receiver.
        var dataOpHits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( var invocation in classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>() )
        {
            var methodName = MethodNameOf( invocation );
            if ( methodName != null && WriteMethods.Contains( methodName ) )
                dataOpHits.Add( methodName );
        }

        // Non-determinism scan: member-access + Random ctor without seed.
        var nonDetHits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( var memberAccess in classDecl.DescendantNodes().OfType<MemberAccessExpressionSyntax>() )
        {
            var key = MemberAccessName( memberAccess );
            if ( key != null && NonDeterminismMembers.Contains( key ) )
                nonDetHits.Add( key );
        }
        foreach ( var oc in classDecl.DescendantNodes().OfType<ObjectCreationExpressionSyntax>() )
        {
            if ( oc.Type is IdentifierNameSyntax id
                 && id.Identifier.ValueText == "Random"
                 && (oc.ArgumentList?.Arguments.Count ?? 0) == 0 )
            {
                nonDetHits.Add( "new Random()" );
            }
        }

        var looksLikeDataOp = dataOpHits.Count > 0;

        return new ClassVerdict(
            ClassName: className,
            FilePath: filePath,
            ExtendsMigration: extendsMigration,
            HasMigrationAttribute: hasMigrationAttr,
            HasDataMigrationAttribute: hasDataMigrationAttr,
            HasStructuralOnlyAttribute: hasStructuralOnlyAttr,
            LooksLikeDataOp: looksLikeDataOp,
            NonDeterminismHits: nonDetHits.ToArray(),
            DataOpHits: dataOpHits.ToArray() );
    }

    private static bool ClassExtendsMigration( ClassDeclarationSyntax classDecl )
    {
        var bases = classDecl.BaseList?.Types;
        if ( bases == null )
            return false;
        foreach ( var bt in bases )
        {
            var name = bt.Type switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                _ => null
            };
            if ( string.Equals( name, "Migration", StringComparison.Ordinal ) )
                return true;
        }
        return false;
    }

    private static IEnumerable<AttributeSyntax> ClassAttributes( ClassDeclarationSyntax classDecl ) =>
        classDecl.AttributeLists.SelectMany( al => al.Attributes );

    private static bool IsAttributeName( AttributeSyntax a, string baseName )
    {
        var name = a.Name switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            _ => null
        };
        return name == baseName || name == $"{baseName}Attribute";
    }

    // Extract the method-name token from an invocation. Handles plain
    // identifiers (`Foo()`), member access (`x.Foo()`), and generic methods
    // (`x.Foo<T>()` -- the generic args ride on the member access's name).
    private static string MethodNameOf( InvocationExpressionSyntax invocation )
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member when member.Name is IdentifierNameSyntax memberId
                => memberId.Identifier.ValueText,
            MemberAccessExpressionSyntax member when member.Name is GenericNameSyntax generic
                => generic.Identifier.ValueText,
            _ => null
        };
    }

    private static string MemberAccessName( MemberAccessExpressionSyntax m )
    {
        var receiver = m.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax inner => MemberAccessName( inner ),
            _ => null
        };
        return receiver == null ? null : $"{receiver}.{m.Name.Identifier.ValueText}";
    }
}
