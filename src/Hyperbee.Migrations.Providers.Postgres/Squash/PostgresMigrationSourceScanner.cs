#nullable enable
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Roslyn-based scanner that walks a directory of migration source files and
/// classifies each <see cref="Migration"/>-derived class against the
/// <c>[DataMigration]</c> / <c>[StructuralOnly]</c> annotation requirement
/// (per ADR-0019 amendment A5).
/// </summary>
/// <remarks>
/// <para>
/// V1 scope: detects
/// <list type="bullet">
///   <item>SQL string literals containing DML keywords
///         (INSERT/UPDATE/DELETE/MERGE/COPY/TRUNCATE/SELECT INTO/CTAS).</item>
///   <item>Non-determinism call sites (DateTime.Now/UtcNow,
///         Guid.NewGuid, Random ctor sans seed, Environment.MachineName,
///         Stopwatch.GetTimestamp, etc.).</item>
/// </list>
/// </para>
/// <para>
/// The output is a per-class verdict; the squash CLI reports unannotated
/// data-op-bearing classes as a refusal-tier diagnostic before generation.
/// </para>
/// </remarks>
public static class PostgresMigrationSourceScanner
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

    private static readonly Regex DmlPattern = new(
        @"\b(INSERT\s+INTO|UPDATE\s+\w|DELETE\s+FROM|TRUNCATE\s+|MERGE\s+INTO|COPY\s+\w+\s+FROM|SELECT\s+.*?\s+INTO\s+|CREATE\s+TABLE\s+\w+\s+AS\s+SELECT)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline );

    private static readonly HashSet<string> NonDeterminismMembers = new( StringComparer.Ordinal )
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTime.Today",
        "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "Guid.NewGuid",
        "Environment.MachineName", "Environment.UserName", "Environment.ProcessId",
        "Stopwatch.GetTimestamp", "Activity.Current"
    };

    /// <summary>
    /// Scans every <c>*.cs</c> file under <paramref name="sourceRoot"/>
    /// (recursively) and returns one <see cref="ClassVerdict"/> per
    /// declared class.
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
            // skip generated obj/bin trees
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
            {
                verdicts.Add( ClassifyClass( classDecl, file ) );
            }
        }

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

        // Walk all string-literal tokens inside method bodies; collect DML hits.
        var dataOpHits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( var literal in classDecl.DescendantNodes().OfType<LiteralExpressionSyntax>() )
        {
            if ( !literal.IsKind( SyntaxKind.StringLiteralExpression ) )
                continue;
            var text = literal.Token.ValueText;
            foreach ( Match m in DmlPattern.Matches( text ) )
            {
                dataOpHits.Add( m.Value.Trim().ToUpperInvariant() );
            }
        }

        // Also walk interpolated string contents.
        foreach ( var interp in classDecl.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>() )
        {
            var text = string.Concat(
                interp.Contents.OfType<InterpolatedStringTextSyntax>().Select( t => t.TextToken.ValueText ) );
            foreach ( Match m in DmlPattern.Matches( text ) )
            {
                dataOpHits.Add( m.Value.Trim().ToUpperInvariant() );
            }
        }

        // Non-determinism scan: member-access and ctor invocations matching
        // the known-non-deterministic API set.
        var nonDetHits = new SortedSet<string>( StringComparer.Ordinal );
        foreach ( var memberAccess in classDecl.DescendantNodes().OfType<MemberAccessExpressionSyntax>() )
        {
            var key = MemberAccessName( memberAccess );
            if ( key != null && NonDeterminismMembers.Contains( key ) )
                nonDetHits.Add( key );
        }
        // Random ctor without a seed — `new Random()`
        foreach ( var oc in classDecl.DescendantNodes().OfType<ObjectCreationExpressionSyntax>() )
        {
            if ( oc.Type is IdentifierNameSyntax id && id.Identifier.ValueText == "Random"
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
            // We only see syntax here, not symbols. The check matches by
            // simple-name to "Migration" — operator's project may use a
            // namespace alias, but the class identifier is what they write.
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
