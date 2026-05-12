using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Roslyn-based scanner that walks a directory of migration source files and
/// classifies each <see cref="Migration"/>-derived class against the
/// <c>[DataMigration]</c> / <c>[StructuralOnly]</c> annotation requirement
/// (per ADR-0019 amendment A5).
/// </summary>
/// <remarks>
/// <para>
/// Couchbase migrations write data via the SDK's KV path
/// (<c>collection.UpsertAsync</c>, <c>InsertAsync</c>, <c>ReplaceAsync</c>,
/// <c>RemoveAsync</c>, <c>MutateInAsync</c>, <c>Binary.AppendAsync</c>, etc.).
/// The scanner detects these invocations and classifies any unannotated class
/// containing them as requiring annotation.
/// </para>
/// <para>
/// V1 detection scope:
/// <list type="bullet">
///   <item>KV write call sites by method name: <c>Upsert*</c>,
///         <c>Insert*</c>, <c>Replace*</c>, <c>Remove*</c>, <c>MutateIn*</c>,
///         <c>Append*</c>, <c>Prepend*</c>, <c>Increment*</c>,
///         <c>Decrement*</c>, <c>Unlock*</c>.</item>
///   <item>Non-determinism call sites (same catalog as Aerospike +
///         OpenSearch + MongoDB).</item>
/// </list>
/// </para>
/// <para>
/// <b>R-P3 OQ resolution -- N1QL Query call-sites:</b> <c>QueryAsync</c> and
/// <c>AnalyticsQueryAsync</c> source-only inspection cannot resolve the SQL
/// value reliably. The scanner does NOT flag these as data ops -- operator
/// annotation is the recorded contract. The data-op classifier surfaces the
/// same default-deny treatment at classification time; both layers agree on
/// the resolution.
/// </para>
/// <para>
/// <b>Receiver-anchoring trade-off (same as the data-op classifier):</b>
/// Couchbase code routes through local <c>collection</c>/<c>bucket</c>/
/// <c>scope</c>/<c>cluster</c> variables. The scanner matches method names
/// without receiver-name filtering. False positives possible: a user class
/// with its own method named <c>UpsertAsync</c> may be flagged. Mitigation:
/// the operator annotates with <c>[StructuralOnly]</c> to suppress. False
/// positives are safer than false negatives under default-deny.
/// </para>
/// <para>
/// <b>Cross-provider hoist candidate (Phase 5):</b> the Migration-extends
/// check, the attribute recognizer, the non-determinism scan, and the
/// invocation-walker shape are now identical across FIVE providers
/// (Postgres, Aerospike, OpenSearch, MongoDB, Couchbase). The receiver-
/// anchoring portion differs per provider but is a one-line override; the
/// rest can be hoisted as <c>MigrationSourceScannerBase</c>.
/// </para>
/// </remarks>
public static class CouchbaseMigrationSourceScanner
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

    // Couchbase KV write method names. Matched by name regardless of receiver
    // (per the receiver-anchoring trade-off above).
    private static readonly HashSet<string> WriteMethods = new( StringComparer.Ordinal )
    {
        "Upsert", "UpsertAsync",
        "Insert", "InsertAsync",
        "Replace", "ReplaceAsync",
        "Remove", "RemoveAsync",
        "MutateIn", "MutateInAsync",
        "Append", "AppendAsync",
        "Prepend", "PrependAsync",
        "Increment", "IncrementAsync",
        "Decrement", "DecrementAsync",
        "Unlock", "UnlockAsync"
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

        var dataOpHits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( var invocation in classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>() )
        {
            var methodName = MethodNameOf( invocation );
            if ( methodName != null && WriteMethods.Contains( methodName ) )
                dataOpHits.Add( methodName );
        }

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
