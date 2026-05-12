#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Roslyn-based scanner that walks a directory of migration source files and
/// classifies each <see cref="Migration"/>-derived class against the
/// <c>[DataMigration]</c> / <c>[StructuralOnly]</c> annotation requirement
/// (per ADR-0019 amendment A5).
/// </summary>
/// <remarks>
/// <para>
/// Aerospike migrations write data via client API calls (<c>_client.Put</c>,
/// <c>_client.Delete</c>, <c>_client.Operate</c>, <c>_client.Touch</c>) rather
/// than DML statement strings. The scanner detects these invocation
/// expressions and classifies any unannotated class containing them as
/// requiring annotation.
/// </para>
/// <para>
/// V1 detection scope:
/// <list type="bullet">
///   <item>Receiver-anchored client write call sites:
///         <c>_client.Put</c>, <c>_client.Delete</c>, <c>_client.Touch</c>.
///         <c>_client.Operate</c> is always flagged because the operations
///         list can be read or write.</item>
///   <item>Non-determinism call sites
///         (<c>DateTime.Now/UtcNow/Today</c>, <c>DateTimeOffset.Now/UtcNow</c>,
///         <c>Guid.NewGuid</c>, <c>Environment.MachineName/UserName/ProcessId</c>,
///         <c>Stopwatch.GetTimestamp</c>, <c>new Random()</c> without seed).</item>
/// </list>
/// </para>
/// <para>
/// Cross-provider hoist candidate: the Migration-extends check, the attribute
/// recognizer, and the non-determinism scan are provider-neutral and identical
/// to the Postgres scanner. When OpenSearch (Phase 2) adds its own scanner the
/// shared shape becomes obvious enough to hoist into a core-lib base class.
/// Tracked under Phase 2 cross-provider participation.
/// </para>
/// </remarks>
public static class AerospikeMigrationSourceScanner
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

    // Receiver-anchored client write methods. Pattern intentionally mirrors
    // AerospikeDataOpClassifier's receiver anchor: the receiver must be a
    // simple identifier named `_client` or `client`. This prevents false
    // positives when the same verb appears on a different receiver
    // (e.g., Operation.Put inside an _client.Operate argument list).
    private static readonly HashSet<string> WriteMethods = new( StringComparer.Ordinal )
    {
        "Put", "Delete", "Touch"
    };

    // Operate is always flagged: the operations list (Operation.put / .get /
    // .add / etc.) determines whether the call is read-or-write, and the
    // Roslyn pass can't classify that statically without type semantics.
    // Falling-through-to-RequiresAnnotation lets the operator make the call.
    private const string OperateMethod = "Operate";

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

        // Walk all method-body invocation expressions; collect client-write hits.
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
            else if ( string.Equals( methodName, OperateMethod, StringComparison.Ordinal ) )
                dataOpHits.Add( $"{receiver}.{OperateMethod} (requires annotation: read vs write)" );
        }

        // Non-determinism scan: member-access matching the known set + Random ctor.
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

    // The receiver expression of `<x>.<member>` -- returns the simple identifier
    // when the receiver is `client` or `_client` (or `this.client` form),
    // otherwise null. The data-op classifier uses the same shape; this scanner
    // mirrors it so static-pass results stay aligned with text-pass results.
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
