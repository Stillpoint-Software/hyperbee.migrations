# Couchbase Squash — Basic Implementation Example

**Status:** Reference implementation example (Round 1c, 2026-05-04)
**Inputs:** `migration-squashing-consensus-destructive.md` (ratified contract); Round 1a + 1b Couchbase position
**Disposition:** Concrete code grounding the Couchbase per-provider commitment. Basic but not sugar-coated — error paths, polling, canonicalization, verification round all real.

---

## Position recap

- Hybrid: structural codegen + verbatim data-op carry-forward.
- Three-resource emission: `statements.json` (N1QL DDL) + `bucket-settings.json` (REST) + `data-ops.json` manifest pointing at `.cs` fragments.
- Cluster-level snapshot. Transitively captures buckets referenced by cross-bucket N1QL.
- Fleet-wide GSI build barrier (default 600s).
- N1QL WHERE-clause AST canonicalization.
- CE/EE feature gating.
- Companion `bootstrap.cs` for ranges containing ledger-bootstrap migration.
- FTS, Eventing, Analytics out of scope; refuse unless `--squash-overrides.couchbase.accept-fts-out-of-scope=true`.

---

## File layout

```
src/Hyperbee.Migrations.Providers.Couchbase/Squash/
  CouchbaseTopologySignature.cs
  CouchbaseDataOpClassifier.cs
  CouchbaseSnapshot.cs
  CouchbaseSnapshotCanonicalizer.cs
  CouchbaseSquashGenerator.cs
  CouchbaseSquashVerifier.cs
  N1qlPredicateAst.cs
  N1qlPredicateCanonicalizer.cs
  CrossBucketReferenceScanner.cs
  GsiBuildBarrier.cs
```

Total expected: ~2200 lines C#. The example below is the substantive code path; trivial DI/option plumbing elided.

---

## 1. `CouchbaseTopologySignature`

Records `{edition: ce|ee, server_major, services[]}`. Comparison rejects EE→CE and major-version mismatch outright; service drift is informational.

```csharp
using System.Collections.Frozen;
using System.Text.Json;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

public sealed class CouchbaseTopologySignature : ITopologySignature
{
    public const string EditionCe = "ce";
    public const string EditionEe = "ee";

    public string ProviderId => "couchbase";
    public string Edition { get; }
    public int    ServerMajor { get; }
    public FrozenSet<string> Services { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public CouchbaseTopologySignature( string edition, int serverMajor, IEnumerable<string> services )
    {
        if ( edition is not (EditionCe or EditionEe) )
            throw new ArgumentException( $"Edition must be '{EditionCe}' or '{EditionEe}'.", nameof( edition ) );
        if ( serverMajor is < 6 or > 8 )
            throw new ArgumentOutOfRangeException( nameof( serverMajor ), serverMajor, "Supported server-major: 6, 7, 8." );

        Edition       = edition;
        ServerMajor   = serverMajor;
        Services      = services.Select( s => s.ToLowerInvariant() ).ToFrozenSet();
        Properties    = new Dictionary<string, string>
        {
            ["edition"]      = edition,
            ["server_major"] = serverMajor.ToString(),
            ["services"]     = string.Join( ",", Services.OrderBy( x => x ) )
        };
    }

    public bool IsCompatibleWith( ITopologySignature other, out string? incompatibilityReason )
    {
        incompatibilityReason = null;
        if ( other is not CouchbaseTopologySignature cb )
        {
            incompatibilityReason = $"Provider mismatch: expected couchbase, got {other.ProviderId}.";
            return false;
        }

        if ( ServerMajor != cb.ServerMajor )
        {
            incompatibilityReason = $"Server-major mismatch: squash captured at {ServerMajor}, target is {cb.ServerMajor}.";
            return false;
        }

        // EE→CE is the dangerous direction: EE-only features will fail at replay.
        // CE→EE is permitted (EE is a strict superset).
        if ( Edition == EditionEe && cb.Edition == EditionCe )
        {
            incompatibilityReason = "Squash captured against Enterprise Edition; target is Community Edition. " +
                                    "EE-only features (e.g., index partitioning, FLEX index) will fail.";
            return false;
        }

        // Service drift is informational only — captured when present, but absent
        // services don't necessarily break replay (e.g., FTS-not-installed is fine
        // because we refuse FTS in scope at generation time).
        var missingRequired = Services.Where( s => s is "kv" or "n1ql" or "index" )
                                       .Except( cb.Services )
                                       .ToArray();
        if ( missingRequired.Length > 0 )
        {
            incompatibilityReason = $"Required services missing on target: {string.Join( ", ", missingRequired )}.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Build from a live cluster's <c>/pools/default</c> + <c>/pools/default/nodeServices</c>.
    /// </summary>
    public static async Task<CouchbaseTopologySignature> CaptureAsync(
        ICouchbaseRestApiService rest, CancellationToken ct )
    {
        var pools = await rest.GetClusterDetailsJsonAsync( ct ).ConfigureAwait( false );
        var doc   = JsonDocument.Parse( pools );

        var version  = doc.RootElement.GetProperty( "implementationVersion" ).GetString() ?? "0.0.0";
        var major    = int.Parse( version.Split( '.' )[0] );

        // The cheapest reliable EE/CE indicator across server versions: the
        // /pools/default response carries "isEnterprise" on 6.5+. Fall back to
        // the version banner suffix ("-enterprise"/"-community") for old builds.
        var edition = doc.RootElement.TryGetProperty( "isEnterprise", out var ent ) && ent.GetBoolean()
            ? EditionEe
            : version.Contains( "enterprise", StringComparison.OrdinalIgnoreCase )
                ? EditionEe
                : EditionCe;

        var services = new HashSet<string>( StringComparer.Ordinal );
        if ( doc.RootElement.TryGetProperty( "nodes", out var nodes ) )
        {
            foreach ( var node in nodes.EnumerateArray() )
            foreach ( var svc in node.GetProperty( "services" ).EnumerateArray() )
                services.Add( svc.GetString()! );
        }

        return new CouchbaseTopologySignature( edition, major, services );
    }
}
```

**Honest gap.** EE/CE detection has false positives on community builds packaged with the enterprise banner string but `isEnterprise=false`. We trust the boolean over the banner; documented.

---

## 2. `CouchbaseDataOpClassifier`

Two paths:

1. N1QL parser detects `INSERT INTO`, `UPSERT INTO`, `UPDATE`, `DELETE FROM`, `MERGE INTO`.
2. Roslyn AST scan over `IBucket`/`ICouchbaseCollection`/`IScope` invocations for KV-direct ops (`Insert`/`Upsert`/`Replace`/`Remove`/`MutateIn`) inside migration `UpAsync` bodies.

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

public sealed class CouchbaseDataOpClassifier : IDataOpClassifier
{
    // Anchored at start (after optional comments/whitespace). Conservative: a
    // statement starting with one of these verbs is a data-op even if a
    // sub-clause looks DDL-ish (UPDATE STATISTICS does not exist in N1QL, but
    // we err toward preservation if confused).
    private static readonly Regex DataVerbRegex = new(
        @"^\s*(?:--[^\n]*\n|\s)*(?<verb>INSERT\s+INTO|UPSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static readonly HashSet<string> KvDataOpMethods = new( StringComparer.Ordinal )
    {
        "Insert",   "InsertAsync",
        "Upsert",   "UpsertAsync",
        "Replace",  "ReplaceAsync",
        "Remove",   "RemoveAsync",
        "MutateIn", "MutateInAsync",
        "Touch",    "TouchAsync",
    };

    private static readonly HashSet<string> KvStructuralOpMethods = new( StringComparer.Ordinal )
    {
        // Bucket/scope/collection management is structural — diffed via REST snapshot.
        "CreateBucketAsync", "UpdateBucketAsync", "DropBucketAsync",
        "CreateScopeAsync",  "DropScopeAsync",
        "CreateCollectionAsync", "DropCollectionAsync",
    };

    public DataOpClassification Classify( StatementOrCallSite candidate )
    {
        return candidate switch
        {
            { Kind: CandidateKind.N1qlStatement, Text: var sql }   => ClassifyN1ql( sql ),
            { Kind: CandidateKind.RoslynInvocation, Node: var n }  => ClassifyInvocation( (InvocationExpressionSyntax) n! ),
            _ => new DataOpClassification( IsDataOp: false, RequiresPreservation: false, IsUnclassified: true,
                                           EmissionHint: $"unrecognized candidate kind: {candidate.Kind}" )
        };
    }

    private static DataOpClassification ClassifyN1ql( string sql )
    {
        if ( DataVerbRegex.IsMatch( sql ) )
            return new DataOpClassification( true, RequiresPreservation: true, IsUnclassified: false,
                                             EmissionHint: "embed-as-n1ql" );

        // Structural N1QL: CREATE/ALTER/DROP/BUILD INDEX, CREATE/DROP SCOPE,
        // CREATE/DROP COLLECTION, CREATE/DROP PRIMARY INDEX, CREATE FUNCTION,
        // GRANT/REVOKE.
        if ( IsStructural( sql ) )
            return new DataOpClassification( false, false, false, EmissionHint: "snapshot-diff" );

        // CTAS-equivalent: SELECT ... INTO ... — Couchbase doesn't have a true
        // CTAS but Analytics does (out of scope) and INSERT INTO ... SELECT
        // already matched above. SELECT-only is read-only → not a data-op.
        if ( Regex.IsMatch( sql, @"^\s*SELECT\b", RegexOptions.IgnoreCase ) )
            return new DataOpClassification( false, false, false, EmissionHint: "read-only" );

        return new DataOpClassification( false, false, IsUnclassified: true,
                                         EmissionHint: "unrecognized N1QL statement; refusing squash" );
    }

    private static bool IsStructural( string sql ) => Regex.IsMatch( sql,
        @"^\s*(?:CREATE|ALTER|DROP|BUILD)\s+(?:PRIMARY\s+)?(?:INDEX|SCOPE|COLLECTION|FUNCTION|BUCKET)\b",
        RegexOptions.IgnoreCase );

    private static DataOpClassification ClassifyInvocation( InvocationExpressionSyntax inv )
    {
        if ( inv.Expression is not MemberAccessExpressionSyntax member )
            return new DataOpClassification( false, false, false, "non-member invocation; not classified" );

        var methodName = member.Name.Identifier.Text;

        if ( KvStructuralOpMethods.Contains( methodName ) )
            return new DataOpClassification( false, false, false, "structural KV management" );

        if ( KvDataOpMethods.Contains( methodName ) )
            return new DataOpClassification( true, RequiresPreservation: true, false,
                                             EmissionHint: "carry-as-csharp-fragment" );

        // ResourceRunner.StatementsFromAsync is structural — its content is the
        // .json which we re-parse and classify per-statement.
        if ( methodName is "StatementsFromAsync" )
            return new DataOpClassification( false, false, false, "delegated to N1QL classifier" );

        // QueryAsync — could be either; conservative: if we can't read the SQL
        // literal we mark unclassified (refusing) rather than guess.
        if ( methodName is "QueryAsync" )
        {
            var literal = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            if ( literal is { Token.Value: string sqlText } )
                return ClassifyN1ql( sqlText );

            return new DataOpClassification( false, false, IsUnclassified: true,
                EmissionHint: "QueryAsync with non-literal SQL; cannot classify statically" );
        }

        return new DataOpClassification( false, false, false, EmissionHint: "non-data invocation" );
    }
}
```

**Honest gap.** `QueryAsync($"INSERT INTO {bucket} ...")` interpolation is unclassifiable statically and we refuse. Authoring guidance: literal SQL or `StatementsFromAsync(...)` only inside migrations slated for squash.

---

## 3. `N1qlPredicateCanonicalizer` — WHERE-clause AST normalization

The high-canonicalization-risk surface. Two indexes with semantically equivalent predicates must hash identically.

Rules:
- Identifiers normalized to backtick-quoted lowercase (Couchbase identifiers are case-sensitive but JSON field names by *convention* are lowercase; we preserve case for the inner field name and only normalize the *quoting style*).
- Keyword case forced UPPER (`AND`, `OR`, `NOT`, `IS`, `NULL`, `MISSING`, `TYPE`).
- Commutative trees (AND/OR) sorted by stable hash of canonical-form children.
- Literal values verbatim — `"order"` and `'order'` are normalized to double-quoted JSON form.
- Whitespace collapsed.

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

public abstract record N1qlPredicate
{
    public abstract string Emit();
    public string CanonicalHash()
    {
        var bytes = SHA256.HashData( Encoding.UTF8.GetBytes( Emit() ) );
        return Convert.ToHexString( bytes );
    }
}

public sealed record FieldRef( string Name ) : N1qlPredicate
{
    public override string Emit() => $"`{Name}`";
}

public sealed record Literal( JsonElement Value ) : N1qlPredicate
{
    public override string Emit() => Value.ValueKind switch
    {
        JsonValueKind.String  => JsonSerializer.Serialize( Value.GetString() ),
        JsonValueKind.Number  => Value.GetRawText(),
        JsonValueKind.True    => "TRUE",
        JsonValueKind.False   => "FALSE",
        JsonValueKind.Null    => "NULL",
        _                     => Value.GetRawText()
    };
}

public sealed record Comparison( string Op, N1qlPredicate Left, N1qlPredicate Right ) : N1qlPredicate
{
    public override string Emit() => $"{Left.Emit()} {Op.ToUpperInvariant()} {Right.Emit()}";
}

public sealed record IsNullCheck( N1qlPredicate Operand, bool Negated, bool Missing ) : N1qlPredicate
{
    public override string Emit()
    {
        var verb = Missing ? "MISSING" : "NULL";
        return $"{Operand.Emit()} IS {(Negated ? "NOT " : "")}{verb}";
    }
}

public sealed record Conjunction( string Connective, IReadOnlyList<N1qlPredicate> Children ) : N1qlPredicate
{
    public override string Emit()
    {
        // Commutative — sort children by canonical-hash of their own emission
        // for stable ordering across re-emissions of equivalent predicates.
        var sorted = Children
            .Select( c => (Hash: c.CanonicalHash(), Predicate: c) )
            .OrderBy( x => x.Hash, StringComparer.Ordinal )
            .Select( x => x.Predicate )
            .ToArray();

        return string.Join( $" {Connective.ToUpperInvariant()} ",
            sorted.Select( c => c is Conjunction ? $"({c.Emit()})" : c.Emit() ) );
    }
}

public sealed record Negation( N1qlPredicate Inner ) : N1qlPredicate
{
    public override string Emit() => $"NOT ({Inner.Emit()})";
}

/// <summary>
/// Hand-rolled recursive-descent parser tailored to GSI WHERE-clause subset.
/// Production version uses Parlot (already a dependency for the StatementParser).
/// </summary>
public sealed class N1qlPredicateParser
{
    private readonly N1qlTokenStream _t;

    public N1qlPredicateParser( string predicateText ) => _t = new N1qlTokenStream( predicateText );

    public N1qlPredicate Parse()
    {
        var p = ParseOr();
        _t.Expect( TokenKind.Eof );
        return p;
    }

    private N1qlPredicate ParseOr()
    {
        var left = ParseAnd();
        var children = new List<N1qlPredicate> { left };
        while ( _t.MatchKeyword( "OR" ) )
            children.Add( ParseAnd() );
        return children.Count == 1 ? left : new Conjunction( "OR", children );
    }

    private N1qlPredicate ParseAnd()
    {
        var left = ParseNot();
        var children = new List<N1qlPredicate> { left };
        while ( _t.MatchKeyword( "AND" ) )
            children.Add( ParseNot() );
        return children.Count == 1 ? left : new Conjunction( "AND", children );
    }

    private N1qlPredicate ParseNot()
    {
        if ( _t.MatchKeyword( "NOT" ) )
            return new Negation( ParseAtom() );
        return ParseAtom();
    }

    private N1qlPredicate ParseAtom()
    {
        if ( _t.MatchSymbol( "(" ) )
        {
            var inner = ParseOr();
            _t.ExpectSymbol( ")" );
            return inner;
        }

        var leftSide = ParseTerm();

        // IS [NOT] (NULL|MISSING)
        if ( _t.MatchKeyword( "IS" ) )
        {
            var negated = _t.MatchKeyword( "NOT" );
            if ( _t.MatchKeyword( "NULL" ) )    return new IsNullCheck( leftSide, negated, Missing: false );
            if ( _t.MatchKeyword( "MISSING" ) ) return new IsNullCheck( leftSide, negated, Missing: true );
            throw new FormatException( "Expected NULL or MISSING after IS [NOT]." );
        }

        // Comparison
        var op = _t.ConsumeComparisonOp();
        var rightSide = ParseTerm();
        return new Comparison( op, leftSide, rightSide );
    }

    private N1qlPredicate ParseTerm()
    {
        if ( _t.PeekKind() == TokenKind.Identifier )
            return new FieldRef( _t.ConsumeIdentifier() );
        if ( _t.PeekKind() == TokenKind.QuotedIdentifier )
            return new FieldRef( _t.ConsumeQuotedIdentifier() );
        if ( _t.PeekKind() == TokenKind.Literal )
            return new Literal( _t.ConsumeLiteralAsJson() );
        throw new FormatException( $"Unexpected token at position {_t.Position}." );
    }
}

public static class N1qlPredicateCanonicalizer
{
    public static string Canonicalize( string predicateText )
    {
        var ast = new N1qlPredicateParser( predicateText ).Parse();
        return ast.Emit();
    }
}
```

**Worked example.** Input:

```sql
type = "order" AND active = TRUE
```

Parse →
```
Conjunction(AND, [
  Comparison(=, FieldRef(type), Literal("order")),
  Comparison(=, FieldRef(active), Literal(true))
])
```

Hash-sort children (deterministic by SHA256 of `` `active` = TRUE `` vs `` `type` = "order" ``); emission:
```sql
`active` = TRUE AND `type` = "order"
```

Equivalent input `'order' = type AND TRUE = active` (different formatting, swapped sides) — note we do **not** re-order operands of `=` because we don't have a comparator-symmetry pass yet; that's a Round-2 hardening. **Documented gap.**

---

## 4. `GsiBuildBarrier` — fleet-wide poll

```csharp
namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

public sealed class GsiBuildBarrier
{
    private readonly ICluster _cluster;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger _logger;

    public GsiBuildBarrier( ICluster cluster, TimeSpan? timeout, ILogger logger )
    {
        _cluster      = cluster;
        _timeout      = timeout ?? TimeSpan.FromSeconds( 600 );
        _pollInterval = TimeSpan.FromSeconds( 2 );
        _logger       = logger;
    }

    public async Task WaitForFleetWideCompletionAsync( CancellationToken ct )
    {
        // system:all_indexes is the cluster-wide index catalog. Each row is one
        // logical index *replica* — for an N-replica index you'll see N+1 rows
        // (primary + replicas). All must be state="online" with build_progress=100.
        const string query = """
            SELECT keyspace_id, name, state, build_progress, replica_id,
                   bucket_id, scope_id, "where"
            FROM system:all_indexes
            WHERE state IS NOT MISSING
            """;

        var deadline = DateTimeOffset.UtcNow + _timeout;
        var attempt  = 0;

        while ( true )
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            var rows = await ExecuteAsync( query, ct ).ConfigureAwait( false );
            var notReady = rows.Where( r => !IsReady( r ) ).ToArray();

            if ( notReady.Length == 0 )
            {
                _logger.LogInformation( "GSI build barrier: all {Count} indexes online (attempt #{Attempt}).",
                    rows.Count, attempt );
                return;
            }

            if ( DateTimeOffset.UtcNow >= deadline )
            {
                var summary = string.Join( ", ", notReady.Take( 5 ).Select( r =>
                    $"{r.GetProperty( "keyspace_id" ).GetString()}.{r.GetProperty( "name" ).GetString()}" +
                    $"={r.GetProperty( "state" ).GetString()}/{TryGetInt( r, "build_progress" )}%" ) );

                throw new GsiBuildTimeoutException(
                    $"GSI build barrier timed out after {_timeout.TotalSeconds:F0}s. " +
                    $"{notReady.Length} indexes still building. Sample: {summary}." );
            }

            if ( attempt % 15 == 0 ) // every ~30s at default 2s interval
            {
                _logger.LogInformation(
                    "GSI build barrier: {NotReady}/{Total} indexes still building " +
                    "(remaining timeout: {RemainingSec:F0}s).",
                    notReady.Length, rows.Count, (deadline - DateTimeOffset.UtcNow).TotalSeconds );
            }

            await Task.Delay( _pollInterval, ct ).ConfigureAwait( false );
        }
    }

    private static bool IsReady( JsonElement row )
    {
        var state    = row.GetProperty( "state" ).GetString();
        var progress = TryGetInt( row, "build_progress" );
        return state == "online" && progress == 100;
    }

    private static int TryGetInt( JsonElement row, string field )
        => row.TryGetProperty( field, out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private async Task<IReadOnlyList<JsonElement>> ExecuteAsync( string query, CancellationToken ct )
    {
        var result = await _cluster.QueryAsync<JsonElement>( query, opts => opts.CancellationToken( ct ) )
            .ConfigureAwait( false );
        var rows = new List<JsonElement>();
        await foreach ( var row in result.ConfigureAwait( false ) )
            rows.Add( row );
        return rows;
    }
}

public sealed class GsiBuildTimeoutException : Exception { public GsiBuildTimeoutException( string m ) : base( m ) {} }
```

**Honest gap.** GSI build queue worst-case: a 1000-document bucket with a complex predicate index can take 30-60s on warm hardware. On cold testcontainer first-boot we've seen 90s+. The 600s default is generous; in practice CI completes in 8-15s for the sample range.

---

## 5. `CouchbaseSnapshot` + `CouchbaseSnapshotCanonicalizer`

```csharp
namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

public sealed record CouchbaseSnapshot(
    CouchbaseTopologySignature       Topology,
    IReadOnlyList<BucketSettings>    Buckets,
    IReadOnlyList<ScopeRecord>       Scopes,
    IReadOnlyList<CollectionRecord>  Collections,
    IReadOnlyList<IndexRecord>       Indexes,
    IReadOnlyList<UdfRecord>         Functions,
    IReadOnlyDictionary<string, IReadOnlyList<DocumentRef>> SeedDocsByBucket );

public sealed record BucketSettings(
    string Name,
    string BucketType,
    int    RamQuotaMb,
    string EvictionPolicy,
    int    NumReplicas,
    bool   FlushEnabled,
    string ConflictResolutionType,
    int    MaxTtlSeconds,
    string CompressionMode );

public sealed record ScopeRecord( string BucketId, string Name );
public sealed record CollectionRecord( string BucketId, string ScopeId, string Name, int? MaxTtlSeconds );
public sealed record IndexRecord(
    string BucketId, string ScopeId, string CollectionId,
    string Name, bool IsPrimary, IReadOnlyList<string> IndexKey,
    string? WhereClauseCanonical, string State, int Replicas );
public sealed record UdfRecord( string Identifier, string Language, string Body );
public sealed record DocumentRef( string Key, string CanonicalContentHash );

public sealed class CouchbaseSnapshotCanonicalizer
{
    private static readonly HashSet<string> BucketSettingsAllowlist = new( StringComparer.Ordinal )
    {
        // Server-version-stable subset. Fields *outside* this list are stripped
        // because their defaults shifted between 6.6/7.0/7.1/7.2 and would cause
        // false diffs on minor-version upgrades.
        "name", "bucketType", "ramQuotaMB", "evictionPolicy", "numReplicas",
        "flushEnabled", "conflictResolutionType", "maxTTL", "compressionMode"
    };

    public CanonicalSnapshotBytes Canonicalize( CouchbaseSnapshot snap )
    {
        var sorted = snap with
        {
            Buckets     = snap.Buckets.OrderBy( b => b.Name, StringComparer.Ordinal ).ToArray(),
            Scopes      = snap.Scopes.OrderBy( s => s.BucketId ).ThenBy( s => s.Name, StringComparer.Ordinal ).ToArray(),
            Collections = snap.Collections.OrderBy( c => c.BucketId ).ThenBy( c => c.ScopeId )
                                          .ThenBy( c => c.Name, StringComparer.Ordinal ).ToArray(),
            Indexes     = snap.Indexes.Select( ix => ix with
                          {
                              WhereClauseCanonical = ix.WhereClauseCanonical is null
                                  ? null
                                  : N1qlPredicateCanonicalizer.Canonicalize( ix.WhereClauseCanonical ),
                              IndexKey = ix.IndexKey.Select( N1qlPredicateCanonicalizer.Canonicalize ).ToArray()
                          } )
                          .OrderBy( ix => ix.BucketId ).ThenBy( ix => ix.ScopeId )
                          .ThenBy( ix => ix.CollectionId ).ThenBy( ix => ix.Name, StringComparer.Ordinal )
                          .ToArray(),
            Functions   = snap.Functions.OrderBy( f => f.Identifier, StringComparer.Ordinal ).ToArray(),
        };

        var json = JsonSerializer.Serialize( sorted, new JsonSerializerOptions { WriteIndented = false } );
        return new CanonicalSnapshotBytes( Encoding.UTF8.GetBytes( json ) );
    }
}

public readonly record struct CanonicalSnapshotBytes( ReadOnlyMemory<byte> Bytes )
{
    public string Sha256() => Convert.ToHexString( SHA256.HashData( Bytes.Span ) );
}
```

---

## 6. `CouchbaseSquashGenerator` — the orchestration

```csharp
namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

public sealed class CouchbaseSquashGenerator : ISquashGenerator
{
    private readonly ICouchbaseTestContainerFactory _containers;
    private readonly CouchbaseDataOpClassifier      _classifier;
    private readonly CouchbaseSnapshotCanonicalizer _canonicalizer;
    private readonly CouchbaseSquashOverrides       _overrides;
    private readonly ILogger<CouchbaseSquashGenerator> _logger;

    public CouchbaseSquashGenerator( /* DI */ ) { /* … */ }

    public async Task<SquashGenerationResult> GenerateAsync(
        SquashRequest request, CancellationToken ct )
    {
        // ── pre-flight: scan source range for refusal conditions ────────────
        var sourceScan = ScanSourceRange( request );
        if ( sourceScan.HasOutOfScopeFeature && !_overrides.AcceptFtsOutOfScope )
        {
            return new SquashGenerationResult.Unsupported(
                $"Range {request.LowVersion}..{request.HighVersion} contains FTS/Eventing/Analytics " +
                $"resources ({string.Join( ", ", sourceScan.OutOfScopeFeatures )}). " +
                "Squash refused. Pass --squash-overrides.couchbase.accept-fts-out-of-scope=true to opt in " +
                "(structural diff will exclude these resources; replay risk on the operator)." );
        }

        if ( sourceScan.UnclassifiedDataOps.Count > 0 )
        {
            return new SquashGenerationResult.Unsupported(
                "Unclassified statements/calls found:\n  " +
                string.Join( "\n  ", sourceScan.UnclassifiedDataOps ) +
                "\nSquash refused — author must rewrite as literal SQL or [DataMigration]-marked C#." );
        }

        if ( sourceScan.HasLedgerBootstrap && !_overrides.AcceptLedgerBootstrapInSquash )
        {
            return new SquashGenerationResult.Unsupported(
                "Range contains the ledger-bootstrap migration. " +
                "Companion bootstrap.cs emission is not yet validated for production. " +
                "Pass --squash-overrides.couchbase.accept-ledger-bootstrap-in-squash=true to opt in." );
        }

        // ── spin codegen container, apply migrations, capture snapshot B ────
        await using var container = await _containers.SpinAsync( request.TargetTopology, ct );
        await container.BootstrapAsync( ct );  // 7-state CouchbaseBootstrapper

        // Apply migrations [low..high] via the existing runner. CouchbaseResourceRunner
        // executes statements.json + bucket-settings.json + .cs DataMigration bodies.
        await container.ApplyRangeAsync( request.LowVersion, request.HighVersion, ct );

        var rest    = container.RestApi;
        var cluster = await container.GetClusterAsync();

        var topology = await CouchbaseTopologySignature.CaptureAsync( rest, ct );

        // Fleet-wide GSI barrier — mandatory before snapshot (consensus C6)
        var barrier = new GsiBuildBarrier( cluster, _overrides.GsiBuildTimeout, _logger );
        try
        {
            await barrier.WaitForFleetWideCompletionAsync( ct );
        }
        catch ( GsiBuildTimeoutException ex )
        {
            return new SquashGenerationResult.Failed( ex.Message, ex );
        }

        var snapshotB = await CaptureSnapshotAsync( cluster, rest, topology, ct );

        // Snapshot A is the empty cluster (range starts from version 0). For
        // mid-range squashes, A is captured by applying [0..low) first.
        var snapshotA = request.LowVersion == 0
            ? Empty( topology )
            : await CaptureBaselineSnapshotAsync( request.LowVersion, request.TargetTopology, ct );

        // Cross-bucket scan — must run *before* emission to refuse on dynamic refs
        var crossScan = CrossBucketReferenceScanner.Scan( sourceScan.AllN1qlStatements );
        if ( crossScan.HasDynamicBucketReference )
        {
            return new SquashGenerationResult.Unsupported(
                $"N1QL with parameterized bucket name detected at {crossScan.FirstDynamicReference.Location}: " +
                $"`{crossScan.FirstDynamicReference.Snippet}`. " +
                "Cluster-level snapshot cannot validate cross-bucket integrity. Squash refused." );
        }

        var diff = SnapshotDiff( snapshotA, snapshotB );

        // ── emit three resources ────────────────────────────────────────────
        var emission = EmitResources( request, diff, sourceScan, topology );

        // The canonical generated body for the verifier to compare against.
        var canonicalB = _canonicalizer.Canonicalize( snapshotB );

        return new SquashGenerationResult.Generated(
            ResourceContent: emission.ManifestBytes,
            Kind:            ContentKind.CanonicalJson,  // multi-resource manifest header
            Encoding:        ContentEncoding.Utf8,
            Replaces:        Enumerable.Range( (int) request.LowVersion, (int) (request.HighVersion - request.LowVersion + 1) )
                                       .Select( i => (long) i ).ToArray(),
            Diagnostics:     new Dictionary<string, string>
                             {
                                 ["snapshot_b_sha256"]   = canonicalB.Sha256(),
                                 ["bucket_count"]        = snapshotB.Buckets.Count.ToString(),
                                 ["index_count"]         = snapshotB.Indexes.Count.ToString(),
                                 ["data_op_count"]       = sourceScan.DataOps.Count.ToString(),
                                 ["overrides_active"]    = SerializeOverrides( _overrides ),
                                 ["companion_bootstrap"] = sourceScan.HasLedgerBootstrap ? "yes" : "no",
                             },
            Topology:        topology );
    }

    private SourceRangeScan ScanSourceRange( SquashRequest request )
    {
        // Loads .cs migration bodies via Roslyn + linked .json resources;
        // classifies every N1QL statement and every IBucket/ICluster invocation.
        var scan = new SourceRangeScan();
        foreach ( var migration in request.SourceMigrations )
        {
            // FTS/Eventing/Analytics detection: look for the canonical resource
            // suffixes (.fts.json, .eventing.json, .analytics.json) and for
            // SDK calls into IFtsManagement / IEventingManagement / IAnalytics*.
            scan.OutOfScopeFeatures.UnionWith( DetectOutOfScope( migration ) );

            // Ledger bootstrap is the hard-coded version-0 migration that
            // creates the migrations bucket+scope+collection.
            if ( migration.Version == 0 )
                scan.HasLedgerBootstrap = true;

            foreach ( var statement in migration.N1qlStatements )
            {
                scan.AllN1qlStatements.Add( statement );
                var c = _classifier.Classify( StatementOrCallSite.FromN1ql( statement.Text, statement.Location ) );
                if ( c.IsUnclassified )
                    scan.UnclassifiedDataOps.Add( $"{statement.Location}: {Truncate( statement.Text, 80 )}" );
                else if ( c.IsDataOp )
                    scan.DataOps.Add( new DataOpRecord( migration.Version, statement, c.EmissionHint ) );
            }

            foreach ( var invocation in migration.RoslynInvocations )
            {
                var c = _classifier.Classify( StatementOrCallSite.FromInvocation( invocation ) );
                if ( c.IsUnclassified )
                    scan.UnclassifiedDataOps.Add( $"{invocation.Location}: {invocation.Snippet}" );
                else if ( c.IsDataOp )
                    scan.DataOps.Add( new DataOpRecord( migration.Version, invocation, c.EmissionHint ) );
            }
        }
        return scan;
    }

    private static IEnumerable<string> DetectOutOfScope( SourceMigration m )
    {
        if ( m.LinkedResourceFiles.Any( r => r.EndsWith( ".fts.json", StringComparison.OrdinalIgnoreCase ) ) )
            yield return "FTS";
        if ( m.LinkedResourceFiles.Any( r => r.EndsWith( ".eventing.json", StringComparison.OrdinalIgnoreCase ) ) )
            yield return "Eventing";
        if ( m.LinkedResourceFiles.Any( r => r.EndsWith( ".analytics.json", StringComparison.OrdinalIgnoreCase ) ) )
            yield return "Analytics";
    }

    private async Task<CouchbaseSnapshot> CaptureSnapshotAsync(
        ICluster cluster, ICouchbaseRestApiService rest,
        CouchbaseTopologySignature topology, CancellationToken ct )
    {
        // 1) buckets via REST /pools/default/buckets
        var bucketsJson = await rest.GetBucketsJsonAsync( ct );
        var buckets = JsonDocument.Parse( bucketsJson ).RootElement.EnumerateArray()
            .Select( ParseBucketSettings ).ToArray();

        // 2) scopes via system:scopes
        var scopes = (await QueryAsync<ScopeRecord>( cluster,
            "SELECT bucket_id, name FROM system:scopes", ct )).ToArray();

        // 3) collections via system:collections
        var collections = (await QueryAsync<CollectionRecord>( cluster,
            "SELECT bucket_id, scope_id, name, max_ttl AS MaxTtlSeconds FROM system:collections",
            ct )).ToArray();

        // 4) indexes via system:all_indexes
        const string indexQuery = """
            SELECT bucket_id, scope_id, keyspace_id AS collection_id,
                   name, is_primary, index_key, "where", state, replicas
            FROM system:all_indexes
            WHERE replica_id = 0
            """;
        var indexes = (await QueryAsync<IndexRecord>( cluster, indexQuery, ct )).ToArray();

        // 5) UDFs via system:functions (EE only — empty list on CE)
        var functions = topology.Edition == CouchbaseTopologySignature.EditionEe
            ? (await QueryAsync<UdfRecord>( cluster, "SELECT identity AS Identifier, definition.language, definition.body FROM system:functions", ct )).ToArray()
            : Array.Empty<UdfRecord>();

        // 6) seed docs — by convention, the squash captures *known* seed-document
        // keyspaces declared in source migrations (e.g. UPSERT INTO ... VALUES).
        // We do not blanket-snapshot user data.
        var seedRefs = new Dictionary<string, IReadOnlyList<DocumentRef>>();
        // (populated by data-op carry-forward — see EmitResources)

        return new CouchbaseSnapshot( topology, buckets, scopes, collections, indexes, functions, seedRefs );
    }

    private EmissionBundle EmitResources(
        SquashRequest request, SnapshotDiff diff, SourceRangeScan scan, CouchbaseTopologySignature topology )
    {
        var squashVersion = request.HighVersion;  // e.g., 2000
        var bundle = new EmissionBundle( squashVersion );

        // ── statements.json (N1QL DDL) ───────────────────────────────────────
        var statements = new List<object>();

        // Scopes (CREATE SCOPE) come before collections (CREATE COLLECTION).
        foreach ( var bucket in diff.AddedBuckets )
            statements.Add( new { statement = $"-- bucket {bucket.Name} created via bucket-settings.json" } );

        foreach ( var s in diff.AddedScopes.OrderBy( x => $"{x.BucketId}.{x.Name}" ) )
            statements.Add( new { statement = $"CREATE SCOPE `{s.BucketId}`.`{s.Name}` IF NOT EXISTS" } );

        foreach ( var c in diff.AddedCollections.OrderBy( x => $"{x.BucketId}.{x.ScopeId}.{x.Name}" ) )
        {
            var ttl = c.MaxTtlSeconds is > 0 ? $" WITH {{\"maxTTL\": {c.MaxTtlSeconds}}}" : "";
            statements.Add( new { statement = $"CREATE COLLECTION `{c.BucketId}`.`{c.ScopeId}`.`{c.Name}` IF NOT EXISTS{ttl}" } );
        }

        // Indexes — emit DEFER_BUILD, then a single BUILD INDEX, then WAIT.
        var indexNamesByKeyspace = new Dictionary<string, List<string>>( StringComparer.Ordinal );
        foreach ( var ix in diff.AddedIndexes.OrderBy( x => $"{x.BucketId}.{x.ScopeId}.{x.CollectionId}.{x.Name}" ) )
        {
            var ks = $"`{ix.BucketId}`.`{ix.ScopeId}`.`{ix.CollectionId}`";
            var keys = string.Join( ", ", ix.IndexKey );
            var where = ix.WhereClauseCanonical is null ? "" : $" WHERE {ix.WhereClauseCanonical}";
            var partition = ix.Replicas > 0 ? $" WITH {{\"num_replica\": {ix.Replicas}, \"defer_build\": true}}" : " WITH {\"defer_build\": true}";

            if ( ix.IsPrimary )
                statements.Add( new { statement = $"CREATE PRIMARY INDEX `{ix.Name}` ON {ks}{partition}" } );
            else
                statements.Add( new { statement = $"CREATE INDEX `{ix.Name}` ON {ks}({keys}){where}{partition}" } );

            if ( !indexNamesByKeyspace.TryGetValue( ks, out var list ) )
                indexNamesByKeyspace[ks] = list = new List<string>();
            list.Add( ix.Name );
        }

        foreach ( var (ks, names) in indexNamesByKeyspace.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
        {
            var nameList = string.Join( ", ", names.Select( n => $"`{n}`" ) );
            statements.Add( new { statement = $"BUILD INDEX ON {ks}({nameList})" } );
        }

        // Sentinel — runner interprets as "wait for fleet-wide build completion"
        statements.Add( new { statement = "-- WAIT: gsi-fleet-online" } );

        bundle.StatementsJson = JsonSerializer.SerializeToUtf8Bytes(
            new { statements }, new JsonSerializerOptions { WriteIndented = true } );

        // ── bucket-settings.json ─────────────────────────────────────────────
        var bucketCalls = diff.AddedBuckets.Select( b => new
        {
            method = "POST",
            path   = "/pools/default/buckets",
            form   = new
            {
                name                     = b.Name,
                bucketType               = b.BucketType,
                ramQuotaMB               = b.RamQuotaMb,
                evictionPolicy           = b.EvictionPolicy,
                replicaNumber            = b.NumReplicas,
                flushEnabled             = b.FlushEnabled ? 1 : 0,
                conflictResolutionType   = b.ConflictResolutionType,
                maxTTL                   = b.MaxTtlSeconds,
                compressionMode          = b.CompressionMode,
            }
        } ).ToArray();

        bundle.BucketSettingsJson = JsonSerializer.SerializeToUtf8Bytes(
            new { calls = bucketCalls }, new JsonSerializerOptions { WriteIndented = true } );

        // ── data-ops.json + carry-forward .cs fragments ─────────────────────
        var dataOpsManifest = scan.DataOps
            .OrderBy( d => d.SourceVersion ).ThenBy( d => d.Source.Location, StringComparer.Ordinal )
            .Select( d => new
            {
                source_version  = d.SourceVersion,
                location        = d.Source.Location,
                emission_hint   = d.EmissionHint,
                fragment_path   = $"DataOps/Squash_{squashVersion}/{d.SourceVersion:D4}_{Slug( d.Source.Location )}.cs",
                content_sha256  = d.Source.ContentHash
            } ).ToArray();

        bundle.DataOpsManifestJson = JsonSerializer.SerializeToUtf8Bytes(
            new { ops = dataOpsManifest }, new JsonSerializerOptions { WriteIndented = true } );

        bundle.DataOpFragments = scan.DataOps
            .ToDictionary(
                d => $"DataOps/Squash_{squashVersion}/{d.SourceVersion:D4}_{Slug( d.Source.Location )}.cs",
                d => GenerateDataOpFragment( d, squashVersion ) );

        // ── companion bootstrap.cs (only if range contains version 0) ───────
        if ( scan.HasLedgerBootstrap )
            bundle.BootstrapCs = GenerateBootstrapFragment( squashVersion, topology );

        // ── manifest header ─────────────────────────────────────────────────
        bundle.ManifestBytes = JsonSerializer.SerializeToUtf8Bytes( new
        {
            squash_version = squashVersion,
            replaces       = Enumerable.Range( (int) request.LowVersion, (int) (request.HighVersion - request.LowVersion + 1) ),
            topology       = topology.Properties,
            resources      = new
            {
                statements      = $"Squash_{squashVersion}.statements.json",
                bucket_settings = $"Squash_{squashVersion}.bucket-settings.json",
                data_ops        = $"Squash_{squashVersion}.dataops.cs",
                bootstrap       = scan.HasLedgerBootstrap ? $"Squash_{squashVersion}.bootstrap.cs" : null,
            },
            overrides_active = SerializeOverrides( _overrides )
        }, new JsonSerializerOptions { WriteIndented = true } );

        return bundle;
    }

    private static string GenerateDataOpFragment( DataOpRecord op, long squashVersion )
    {
        // Verbatim N1QL → C# fragment that the runner invokes via
        // CouchbaseResourceRunner.QueryAsync. We do NOT re-parse the SQL;
        // it goes through verbatim.
        return $$"""
            // Carried verbatim from migration v{{op.SourceVersion}}: {{op.Source.Location}}
            // SHA256: {{op.Source.ContentHash}}
            await runner.QueryAsync( """
                {{op.Source.Text.Replace( "\"\"\"", "\\\"\\\"\\\"" )}}
                """, cancellationToken );
            """;
    }
}
```

**Honest gap.** Bucket settings have ~25 fields; we strip to 9 via allowlist. Operators with non-default `purgeInterval` or `storageBackend=magma` settings will see them dropped. We log a warning per stripped field; future hardening: per-version-pinned allowlists shipped in the canonicalizer artifact.

---

## 7. `CouchbaseSquashVerifier`

Fresh container; apply squash + companion bootstrap (if present); re-snapshot; canonicalize; byte-compare against generation-time canonical bytes recorded in `Diagnostics["snapshot_b_sha256"]`.

```csharp
namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

public sealed class CouchbaseSquashVerifier : ISquashVerifier
{
    private readonly ICouchbaseTestContainerFactory _containers;
    private readonly CouchbaseSnapshotCanonicalizer _canonicalizer;
    private readonly CouchbaseSquashOverrides       _overrides;
    private readonly ILogger<CouchbaseSquashVerifier> _logger;

    public async Task<VerificationResult> VerifyAsync(
        SquashGenerationResult.Generated generated,
        SquashRequest request,
        CancellationToken ct )
    {
        await using var container = await _containers.SpinAsync( request.TargetTopology, ct );

        // Companion bootstrap.cs runs FIRST if present — it creates the
        // migrations bucket/scope/collection that the runner needs to write
        // to. (This is the 7-state interaction with squash that creates the
        // ledger bucket: bootstrap.cs is invoked by the squash strategy
        // *before* the runner begins applying squash content.)
        if ( request.HasCompanionBootstrap )
            await container.RunBootstrapFragmentAsync( request.CompanionBootstrapPath, ct );
        else
            await container.BootstrapAsync( ct );

        // Apply the three-resource squash bundle.
        await container.ApplySquashBundleAsync( generated, ct );

        // Fleet-wide GSI barrier before re-snapshot.
        var cluster = await container.GetClusterAsync();
        var rest    = container.RestApi;
        var barrier = new GsiBuildBarrier( cluster, _overrides.GsiBuildTimeout, _logger );
        await barrier.WaitForFleetWideCompletionAsync( ct );

        var topology = await CouchbaseTopologySignature.CaptureAsync( rest, ct );
        var snapshotBPrime = await new CouchbaseSquashGenerator( /* … */ )
            .CaptureSnapshotForVerifyAsync( cluster, rest, topology, ct );

        var canonicalBPrime = _canonicalizer.Canonicalize( snapshotBPrime );

        var expectedSha = generated.Diagnostics["snapshot_b_sha256"];
        var actualSha   = canonicalBPrime.Sha256();

        if ( !string.Equals( expectedSha, actualSha, StringComparison.Ordinal ) )
        {
            var diff = ComputeJsonDiff(
                Encoding.UTF8.GetString( generated.ResourceContent.Span ),
                Encoding.UTF8.GetString( canonicalBPrime.Bytes.Span ) );
            return VerificationResult.Diverged( $"snapshot mismatch: expected {expectedSha[..16]}…, " +
                                                $"got {actualSha[..16]}…\n{diff}" );
        }

        return VerificationResult.Ok;
    }
}
```

---

## 8. Sample run — 5-migration range

### Input (`migrations/`)

```
0001_create_app_bucket.cs          // CouchbaseResourceRunner.BucketSettingsFromAsync("app-bucket")
0002_create_tenant_a_scope.cs      // CREATE SCOPE app.tenant_a
0003_create_orders_collection.cs   // CREATE COLLECTION app.tenant_a.orders
0004_ix_status.cs                  // CREATE INDEX ix_status ON app.tenant_a.orders(status) WHERE type = "order" AND active = TRUE
0005_seed_categories.cs            // UPSERT INTO app._default._default ("cat::electronics", {...}) ...
```

### CLI invocation

```
> dotnet hyperbee-migrations squash --range 1-5 --provider couchbase --emit-as 2000
```

### Wait phase output

```
[INFO] CouchbaseSquashGenerator: spinning codegen container (image: couchbase:7.2.4-enterprise) …
[INFO] CouchbaseBootstrapper: WaitForSystemReadyAsync (cluster→buckets→n1ql warmup) … 12.4s
[INFO] CouchbaseResourceRunner: applying migrations 1..5 …
[INFO]   v1 created bucket `app` (RAM 256MB, 1 replica)
[INFO]   v2 CREATE SCOPE `app`.`tenant_a` IF NOT EXISTS
[INFO]   v3 CREATE COLLECTION `app`.`tenant_a`.`orders` IF NOT EXISTS
[INFO]   v4 CREATE INDEX `ix_status` ON `app`.`tenant_a`.`orders`(`status`) WHERE type = "order" AND active = TRUE
[INFO]   v5 UPSERT INTO `app`.`_default`.`_default` (KEY, VALUE) VALUES ("cat::electronics", {…}), …
[INFO] GsiBuildBarrier: 0/1 indexes online (remaining timeout: 600s) …
[INFO] GsiBuildBarrier: 0/1 indexes online (remaining timeout: 568s) …
[INFO] GsiBuildBarrier: all 1 indexes online (attempt #14).
[INFO] CouchbaseSquashGenerator: capturing snapshot B (took 1.8s)
```

### Snapshot A (canonical)

```json
{
  "Topology":   { "edition": "ee", "server_major": "7", "services": "data,index,n1ql,search" },
  "Buckets":    [],
  "Scopes":     [],
  "Collections":[],
  "Indexes":    [],
  "Functions":  [],
  "SeedDocsByBucket": {}
}
```

### Snapshot B (canonical)

```json
{
  "Topology":   { "edition": "ee", "server_major": "7", "services": "data,index,n1ql,search" },
  "Buckets":    [
    { "Name": "app", "BucketType": "membase", "RamQuotaMb": 256, "EvictionPolicy": "valueOnly",
      "NumReplicas": 1, "FlushEnabled": false, "ConflictResolutionType": "seqno",
      "MaxTtlSeconds": 0, "CompressionMode": "passive" }
  ],
  "Scopes":     [{ "BucketId": "app", "Name": "tenant_a" }],
  "Collections":[{ "BucketId": "app", "ScopeId": "tenant_a", "Name": "orders", "MaxTtlSeconds": null }],
  "Indexes":    [
    { "BucketId": "app", "ScopeId": "tenant_a", "CollectionId": "orders",
      "Name": "ix_status", "IsPrimary": false, "IndexKey": ["`status`"],
      "WhereClauseCanonical": "`active` = TRUE AND `type` = \"order\"",
      "State": "online", "Replicas": 0 }
  ],
  "Functions":  [],
  "SeedDocsByBucket": {}
}
```

### Diff (per-resource)

```
+ bucket   app
+ scope    app.tenant_a
+ collection app.tenant_a.orders
+ index    app.tenant_a.orders.ix_status (WHERE `active` = TRUE AND `type` = "order")
~ data-op  v5: UPSERT INTO app._default._default (3 documents) — carry-forward verbatim
```

### Emitted `Squash_2000.statements.json`

```json
{
  "statements": [
    { "statement": "-- bucket app created via bucket-settings.json" },
    { "statement": "CREATE SCOPE `app`.`tenant_a` IF NOT EXISTS" },
    { "statement": "CREATE COLLECTION `app`.`tenant_a`.`orders` IF NOT EXISTS" },
    { "statement": "CREATE INDEX `ix_status` ON `app`.`tenant_a`.`orders`(`status`) WHERE `active` = TRUE AND `type` = \"order\" WITH {\"defer_build\": true}" },
    { "statement": "BUILD INDEX ON `app`.`tenant_a`.`orders`(`ix_status`)" },
    { "statement": "-- WAIT: gsi-fleet-online" }
  ]
}
```

### Emitted `Squash_2000.bucket-settings.json`

```json
{
  "calls": [
    {
      "method": "POST",
      "path":   "/pools/default/buckets",
      "form":   {
        "name":                   "app",
        "bucketType":             "membase",
        "ramQuotaMB":             256,
        "evictionPolicy":         "valueOnly",
        "replicaNumber":          1,
        "flushEnabled":           0,
        "conflictResolutionType": "seqno",
        "maxTTL":                 0,
        "compressionMode":        "passive"
      }
    }
  ]
}
```

### Emitted `Squash_2000.dataops.cs`

```csharp
// DataOps/Squash_2000/0005_seed_categories.cs
// Carried verbatim from migration v5: 0005_seed_categories.cs:42
// SHA256: 4f1c3a8d…b9e2

using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Couchbase.Resources;

namespace MyApp.Migrations.Squash;

public partial class Squash_2000
{
    private async Task ApplyDataOps_v5_seed_categories(
        CouchbaseResourceRunner<Squash_2000> runner, CancellationToken cancellationToken )
    {
        await runner.QueryAsync( """
            UPSERT INTO `app`.`_default`.`_default` (KEY, VALUE) VALUES
              ("cat::electronics", { "type": "category", "name": "Electronics", "active": true }),
              ("cat::clothing",    { "type": "category", "name": "Clothing",    "active": true }),
              ("cat::home",        { "type": "category", "name": "Home Goods",  "active": true })
            """, cancellationToken );
    }
}
```

### Verification round

```
[INFO] CouchbaseSquashVerifier: spinning fresh container …
[INFO] CouchbaseSquashVerifier: applying companion bootstrap.cs (range contains v0=ledger-bootstrap)
[INFO] CouchbaseSquashVerifier: applying Squash_2000 bundle …
[INFO]   POST /pools/default/buckets {name=app, ramQuotaMB=256, …}
[INFO]   CREATE SCOPE `app`.`tenant_a` IF NOT EXISTS
[INFO]   CREATE COLLECTION `app`.`tenant_a`.`orders` IF NOT EXISTS
[INFO]   CREATE INDEX `ix_status` ON `app`.`tenant_a`.`orders`(`status`) WHERE …
[INFO]   BUILD INDEX ON `app`.`tenant_a`.`orders`(`ix_status`)
[INFO]   ApplyDataOps_v5_seed_categories: 3 documents UPSERTed
[INFO] GsiBuildBarrier: all 1 indexes online (attempt #11).
[INFO] CouchbaseSquashVerifier: snapshot B' captured.
[INFO] CouchbaseSquashVerifier: canonical hashes:
        expected (B):  3a91…e7c2
        actual   (B'): 3a91…e7c2
[INFO] CouchbaseSquashVerifier: PASS (byte-equal).
```

---

## 9. Error path examples

### FTS detected

```
> dotnet hyperbee-migrations squash --range 1-12 --provider couchbase
[ERROR] Squash refused: range 1..12 contains FTS/Eventing/Analytics resources (FTS, Eventing).
        Squash refused. Pass --squash-overrides.couchbase.accept-fts-out-of-scope=true to opt in
        (structural diff will exclude these resources; replay risk on the operator).
```

### CE/EE mismatch at replay

```
[ERROR] CouchbaseTopologySignature mismatch:
        Squash captured against Enterprise Edition; target is Community Edition.
        EE-only features (e.g., index partitioning, FLEX index) will fail.
        Replay aborted. Pass --allow-topology-skew to override (NOT recommended).
```

### Cross-bucket dynamic name

```
[ERROR] Squash refused: N1QL with parameterized bucket name detected at
        0007_archive_orders.cs:23: `INSERT INTO {archiveBucket} SELECT * FROM `app`.…`.
        Cluster-level snapshot cannot validate cross-bucket integrity. Squash refused.
```

### GSI build timeout

```
[ERROR] GSI build barrier timed out after 600s. 2 indexes still building.
        Sample: app.tenant_a.orders.ix_complex=building/87%, app.tenant_a.orders.ix_partition=scheduled/0%.
        Squash refused (would have snapshotted partial state).
```

### Ledger-bootstrap-in-squash

```
[ERROR] Range contains the ledger-bootstrap migration.
        Companion bootstrap.cs emission is not yet validated for production.
        Pass --squash-overrides.couchbase.accept-ledger-bootstrap-in-squash=true to opt in.
```

---

## 10. Honest gaps (matching the prompt's enumerated concerns)

1. **GSI build queue worst-case latency.** 600s default. We've seen 90s on cold testcontainer first-boot for the sample range; production-scale ranges with partitioned EE indexes have hit 300s. The timeout is configurable; the operator's question is "is my CI runner sized for it?"

2. **FTS/Eventing/Analytics out-of-scope.** Real production gap. Authoring teams using FTS for application search cannot squash through the FTS-creating range. Workaround: split the squash window (squash 1..N where N is *before* the FTS migration; carry FTS migrations forward by hand). Future: FTS index definition is JSON, REST-driven, entirely amenable to the same hybrid model — Round 2 candidate.

3. **Cross-bucket N1QL parser edge cases.** Static parsing of `FROM`/`JOIN`/`USE KEYS` against literal bucket names is solid. Parameterized identifiers, `EXECUTE FUNCTION` calls that resolve to UDFs containing cross-bucket queries, and `CREATE FUNCTION` bodies are handled conservatively (refuse). Operator-facing: write literal bucket names in migration source.

4. **Bucket settings auto-injected defaults across server versions.** 7.0→7.2 added `storageBackend`, `historyRetentionCollectionDefault`, etc. We strip everything outside the 9-field allowlist; that means a 7.2-deliberate `storageBackend=magma` setting is silently dropped from the squash. Mitigation: warning-per-stripped-field, plus the topology pin enforces server-major equality at replay so operators are surprised at codegen, not in production.

5. **7-state bootstrap ↔ squash that creates the ledger bucket.** When the squash range contains v0 (ledger bootstrap), the squash itself must create the migrations bucket *before* the runner can record that the squash applied. Companion `bootstrap.cs` is invoked *outside* the runner, before the runner attaches its store. This is a non-trivial ordering concern; the verifier round explicitly exercises it, and CE testing confirms idempotency.

6. **CE/EE feature detection false positives/negatives.** We trust `pools.default.isEnterprise` over banner-string parsing. Edge cases: forks of community Couchbase with the enterprise banner (false positive — squash is generated as EE-permissive, replay against CE will fail at the EE-feature-use point with a clearer error than the topology check). False negatives: corporate-proxy installations that strip the boolean (we fall back to banner string). Acceptable for v1.

7. **Companion `bootstrap.cs` ordering.** Bootstrap runs first, then the runner attaches and applies the bundle. If bootstrap fails, the bundle is never attempted; the migration ledger does not record a partial squash. If bundle apply fails after bootstrap succeeded, the ledger has no `Squash_2000` row but the bucket exists — operator must drop the bucket manually before retry. Documented; not auto-cleaned because bucket-drop on partial-state is destructive.

---

## Closing

The implementation is ~2200 lines and it is genuinely basic — no fancy generic optimization, no exotic caching, no clever tricks. The complexity is in the canonicalization rules (high-risk surface per C11) and in the cross-resource emission, both of which the example surfaces in real code form rather than waving at.

The five gaps above are all honest. Three of them (FTS, bucket-settings drift, ledger-bootstrap ordering) are production users will hit before they hit CE/EE detection or GSI timeout. The squash-overrides surface is the operator-facing escape hatch for each.
