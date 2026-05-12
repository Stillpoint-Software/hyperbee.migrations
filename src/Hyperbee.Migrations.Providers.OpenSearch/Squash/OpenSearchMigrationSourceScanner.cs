#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// Roslyn-based scanner that walks a directory of migration source files and
/// classifies each <see cref="Migration"/>-derived class against the
/// <c>[DataMigration]</c> / <c>[StructuralOnly]</c> annotation requirement
/// (per ADR-0019 amendment A5).
/// </summary>
/// <remarks>
/// <para>
/// OpenSearch migrations write data via the client API (<c>_client.IndexAsync</c>,
/// <c>BulkAsync</c>, <c>UpdateByQueryAsync</c>, etc.) and via the REINDEX
/// statement. The scanner detects both shapes and classifies any unannotated
/// class containing them as requiring annotation.
/// </para>
/// <para>
/// V1 detection scope:
/// <list type="bullet">
///   <item>Receiver-anchored client write call sites: <c>Index*</c>,
///         <c>IndexDocument*</c>, <c>Update*</c>, <c>UpdateByQuery*</c>,
///         <c>Delete*</c>, <c>DeleteByQuery*</c>, <c>Bulk*</c>,
///         <c>Reindex*</c>. The receiver must be a simple identifier named
///         <c>client</c> or <c>_client</c> (the codebase convention) -- this
///         keeps the matcher from firing on sub-client paths
///         (<c>_client.Indices.Create</c>) which are structural.</item>
///   <item>Non-determinism call sites (same catalog as Aerospike):
///         <c>DateTime.Now/UtcNow/Today</c>, <c>DateTimeOffset.Now/UtcNow</c>,
///         <c>Guid.NewGuid</c>, <c>Random.Shared</c>,
///         <c>Environment.MachineName/UserName/ProcessId</c>,
///         <c>Stopwatch.GetTimestamp</c>, <c>new Random()</c> without seed.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cross-provider hoist candidate (Phase 5):</b> the Migration-extends
/// check, the attribute recognizer, and the non-determinism scan are
/// provider-neutral and now identical across THREE providers
/// (<see cref="PostgresMigrationSourceScanner"/> in Postgres,
/// <see cref="Hyperbee.Migrations.Providers.Aerospike.Squash.AerospikeMigrationSourceScanner"/>,
/// and this one). When Phase 5 release prep runs, hoist the shared shape
/// into a <c>MigrationSourceScannerBase</c> in the core library so the
/// per-provider scanners only override the data-op-detection portion.
/// </para>
/// </remarks>
public static class OpenSearchMigrationSourceScanner
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
        /// <summary>
        /// True when the class extends <see cref="Migration"/> AND looks like
        /// a data op AND lacks both <c>[DataMigration]</c> and
        /// <c>[StructuralOnly]</c>. Per ADR-0019 A5 the squash CLI refuses
        /// generation when any subsumed migration matches.
        /// </summary>
        public bool RequiresAnnotation =>
            ExtendsMigration
            && HasMigrationAttribute
            && (LooksLikeDataOp || NonDeterminismHits.Count > 0)
            && !HasDataMigrationAttribute
            && !HasStructuralOnlyAttribute;
    }

    // Receiver-anchored write methods. The receiver must be exactly `client`
    // or `_client` (codebase convention); sub-client paths like
    // `_client.Indices.CreateAsync` are NOT data ops -- they're structural --
    // and the receiver-name filter excludes them automatically.
    private static readonly HashSet<string> WriteMethods = new( StringComparer.Ordinal )
    {
        // Document index ops
        "Index", "IndexAsync", "IndexDocument", "IndexDocumentAsync",
        // Document update ops
        "Update", "UpdateAsync", "UpdateByQuery", "UpdateByQueryAsync",
        // Document delete ops
        "Delete", "DeleteAsync", "DeleteByQuery", "DeleteByQueryAsync",
        // Bulk variants
        "Bulk", "BulkAsync", "BulkAll",
        // Reindex variants (document movement)
        "Reindex", "ReindexAsync", "ReindexOnServer", "ReindexOnServerAsync"
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

    /// <summary>
    /// Scans every <c>*.cs</c> file under <paramref name="sourceRoot"/>
    /// (recursively) and returns one <see cref="ClassVerdict"/> per declared
    /// class. Skips files under <c>obj/</c> and <c>bin/</c> trees.
    /// </summary>
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

    /// <summary>
    /// Classifies a single source string. Exposed so callers (CLI, in-process
    /// strategy refusal gate) can scan a single migration file without walking
    /// a directory tree.
    /// </summary>
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

        // Walk method-body invocation expressions; collect client-write hits.
        var dataOpHits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( var invocation in classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>() )
        {
            if ( invocation.Expression is not MemberAccessExpressionSyntax memberAccess )
                continue;

            var receiver = ReceiverIdentifierName( memberAccess.Expression );
            if ( !IsClientReceiver( receiver ) )
                continue;

            var methodName = memberAccess.Name.Identifier.ValueText;
            if ( WriteMethods.Contains( methodName ) )
                dataOpHits.Add( $"{receiver}.{methodName}" );
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

    private static string? ReceiverIdentifierName( ExpressionSyntax receiverExpr )
    {
        return receiverExpr switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax inner when inner.Name is IdentifierNameSyntax memberId
                => memberId.Identifier.ValueText,
            _ => null
        };
    }

    private static bool IsClientReceiver( string? receiverName )
    {
        return string.Equals( receiverName, "_client", StringComparison.Ordinal )
            || string.Equals( receiverName, "client", StringComparison.Ordinal );
    }

    private static string? MemberAccessName( MemberAccessExpressionSyntax m )
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
