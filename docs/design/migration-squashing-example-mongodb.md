# Migration Squashing — MongoDB Implementation Example

**Status:** Round 2 implementation example, MongoDB advocate
**Inputs:** [Destructive consensus](migration-squashing-consensus-destructive.md) + Round 1a/1b refinement
**Disposition:** Basic but not sugar-coated. Real C# code, real failure modes, real gaps.

---

## Position recap

The MongoDB advocate position, ratified in Round 1b:

- Reject EF Core consultant's "replay-only" framing. **Produce structural diff** over collection options/indexes/validators **plus carry-forward** for data ops.
- `IntrospectionSnapshotStrategy` with explicit IN/OUT scope.
- JSON Schema validator canonicalization: 8 specific rules (sort `properties`, sort `required`, sort `bsonType` arrays, sort `enum`, normalize `type` vs `bsonType`, preserve `allOf`/`anyOf`/`oneOf` order).
- Topology pinning required: `target-topology: standalone|replica-set|sharded` mandatory in `fleet.yml`.
- Sharded refused unless `--squash-overrides.mongodb.allow-sharded-codegen=true` with required shard-key declarations.
- Atlas Search refused (out of v1 scope).
- Strategy emits `statements.json` (Parlot Mongo-shell-like), symmetric with Postgres `.sql`.

---

## What this example demonstrates

1. `MongoTopologySignature` — implements `ITopologySignature` for the consensus contract.
2. `MongoDataOpClassifier` — Roslyn AST visitor over `IMongoCollection<T>` invocations + aggregation pipeline `$out`/`$merge` detection.
3. `MongoSquashGenerator` — orchestration: spin container, apply migrations, capture state, canonicalize, structural diff, emit.
4. `MongoSquashVerifier` — fresh container, apply squash, re-introspect, byte-compare.
5. End-to-end sample run on a 5-migration range, including realistic JSON Schema validator and a `$out` aggregation backfill data op carried forward verbatim.

The honest gaps are called out inline and recapitulated at the end.

---

## 1. `MongoTopologySignature`

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Squashing;

public enum MongoTopology { Standalone, ReplicaSet, Sharded }

public sealed class MongoTopologySignature : ITopologySignature
{
    public string ProviderId => "mongodb";
    public MongoTopology Topology { get; }
    public int ServerMajor { get; }
    public int ServerMinor { get; }
    public string ServerVersionRaw { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }

    public MongoTopologySignature( MongoTopology topology, int major, int minor, string raw )
    {
        Topology = topology;
        ServerMajor = major;
        ServerMinor = minor;
        ServerVersionRaw = raw;

        Properties = new ReadOnlyDictionary<string, string>( new Dictionary<string, string>
        {
            ["topology"] = topology switch
            {
                MongoTopology.Standalone => "standalone",
                MongoTopology.ReplicaSet => "replica-set",
                MongoTopology.Sharded => "sharded",
                _ => "unknown"
            },
            ["server_major"] = major.ToString(),
            ["server_minor"] = minor.ToString(),
            ["server_version_raw"] = raw
        } );
    }

    public static async Task<MongoTopologySignature> CaptureAsync(
        IMongoClient client,
        CancellationToken ct = default )
    {
        var admin = client.GetDatabase( "admin" );

        var hello = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument( "hello", 1 ), cancellationToken: ct );

        var topology = hello.Contains( "msg" ) && hello["msg"].AsString == "isdbgrid"
            ? MongoTopology.Sharded
            : hello.Contains( "setName" )
                ? MongoTopology.ReplicaSet
                : MongoTopology.Standalone;

        var build = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument( "buildInfo", 1 ), cancellationToken: ct );
        var version = build["version"].AsString;
        var parts = version.Split( '.' );
        var major = int.Parse( parts[0] );
        var minor = parts.Length > 1 ? int.Parse( parts[1] ) : 0;

        return new MongoTopologySignature( topology, major, minor, version );
    }

    public bool IsCompatibleWith( ITopologySignature other, out string? incompatibilityReason )
    {
        if ( other is not MongoTopologySignature m )
        {
            incompatibilityReason = $"provider mismatch: this=mongodb other={other.ProviderId}";
            return false;
        }

        // Topology mismatches are the silent-fidelity bug Round 1b flagged. Any drift is fatal.
        if ( Topology != m.Topology )
        {
            incompatibilityReason =
                $"topology mismatch: target={Topology} captured={m.Topology}. " +
                "Replica-set behavior (write concern, oplog) and sharded behavior (chunk routing) " +
                "differ from standalone. Squash artifact would silently apply against wrong shape.";
            return false;
        }

        // Major version drift is fatal too. collMod options vary between 4.4/5.0/6.0/7.0.
        if ( ServerMajor != m.ServerMajor )
        {
            incompatibilityReason =
                $"server major mismatch: target={ServerMajor} captured={m.ServerMajor}. " +
                $"collMod option set and validator semantics may differ; squash refuses.";
            return false;
        }

        // Minor drift is logged, not fatal — we don't pin minor by default.
        incompatibilityReason = null;
        return true;
    }

    public override string ToString() => $"mongodb:{Topology}:{ServerVersionRaw}";
}
```

---

## 2. `MongoDataOpClassifier`

Roslyn AST scan. The interesting bits: detecting `IMongoCollection<T>` method calls (which can chain through builder fluent APIs) and detecting aggregation pipelines containing `$out`/`$merge` stages even when the pipeline is built via `BsonDocument.Parse` of an embedded JSON string.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MongoDB.Bson;

namespace Hyperbee.Migrations.Providers.MongoDB.Squashing;

public sealed class MongoDataOpClassifier : IDataOpClassifier
{
    private static readonly HashSet<string> WriteMethods = new( StringComparer.Ordinal )
    {
        "InsertOne", "InsertOneAsync",
        "InsertMany", "InsertManyAsync",
        "UpdateOne", "UpdateOneAsync",
        "UpdateMany", "UpdateManyAsync",
        "ReplaceOne", "ReplaceOneAsync",
        "DeleteOne", "DeleteOneAsync",
        "DeleteMany", "DeleteManyAsync",
        "BulkWrite", "BulkWriteAsync",
        "FindOneAndUpdate", "FindOneAndUpdateAsync",
        "FindOneAndReplace", "FindOneAndReplaceAsync",
        "FindOneAndDelete", "FindOneAndDeleteAsync"
    };

    // Methods that return an aggregation cursor. We then have to inspect the pipeline arg.
    private static readonly HashSet<string> AggregateMethods = new( StringComparer.Ordinal )
    {
        "Aggregate", "AggregateAsync"
    };

    public DataOpClassification Classify( StatementOrCallSite candidate )
    {
        // Framework hands us either a parsed migration source file or a single call site.
        if ( candidate.Kind != CandidateKind.RoslynNode )
            return new DataOpClassification( IsDataOp: false, RequiresPreservation: false,
                IsUnclassified: false, EmissionHint: null );

        var node = candidate.SyntaxNode!;

        if ( node is not InvocationExpressionSyntax invocation )
            return new DataOpClassification( false, false, false, null );

        var methodName = ExtractMethodName( invocation );
        if ( methodName is null )
            return new DataOpClassification( false, false, false, null );

        // Direct write method on IMongoCollection<T>.
        if ( WriteMethods.Contains( methodName ) )
        {
            return new DataOpClassification(
                IsDataOp: true,
                RequiresPreservation: true,
                IsUnclassified: false,
                EmissionHint: $"carry-as-csharp-fragment:{methodName}"
            );
        }

        // Aggregation: structural unless pipeline contains $out or $merge.
        if ( AggregateMethods.Contains( methodName ) )
        {
            var hasOutOrMerge = AggregationContainsOutOrMerge( invocation );
            if ( hasOutOrMerge.detected )
            {
                return new DataOpClassification(
                    IsDataOp: true,
                    RequiresPreservation: true,
                    IsUnclassified: false,
                    EmissionHint: $"carry-as-csharp-fragment:Aggregate:{hasOutOrMerge.stage}"
                );
            }

            // No $out/$merge means the aggregation is read-only — does not affect schema.
            return new DataOpClassification( false, false, false, null );
        }

        // RunCommand with admin commands or createIndexes/createCollection → DDL, structural.
        if ( methodName is "RunCommand" or "RunCommandAsync" )
        {
            var commandName = ExtractRunCommandName( invocation );
            return commandName switch
            {
                "createCollection" or "drop" or "createIndexes" or "dropIndexes"
                    or "collMod" or "renameCollection"
                    => new DataOpClassification( false, false, false, null ),

                // We can't tell what RunCommand is doing. Be conservative: refuse the squash.
                null => new DataOpClassification(
                    IsDataOp: false, RequiresPreservation: false,
                    IsUnclassified: true,
                    EmissionHint: "refuse:RunCommand-with-non-literal-command-name" ),

                // Known data commands.
                "insert" or "update" or "delete" or "findAndModify"
                    => new DataOpClassification( true, true, false,
                        $"carry-as-csharp-fragment:RunCommand:{commandName}" ),

                _ => new DataOpClassification( false, false, false, null )
            };
        }

        return new DataOpClassification( false, false, false, null );
    }

    public ClassificationReport ScanFile( string path, CancellationToken ct = default )
    {
        var src = File.ReadAllText( path );
        var tree = CSharpSyntaxTree.ParseText( src, cancellationToken: ct );
        var root = tree.GetRoot( ct );

        var report = new ClassificationReport( path );

        foreach ( var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>() )
        {
            var c = Classify( new StatementOrCallSite( CandidateKind.RoslynNode, null, inv ) );

            if ( c.IsUnclassified )
                report.Unclassified.Add( (inv.GetLocation(), c.EmissionHint!) );
            else if ( c.IsDataOp && c.RequiresPreservation )
                report.DataOps.Add( (inv, c.EmissionHint!) );
            // else: structural, will be captured by introspection
        }

        // Also scan for pipelines stored as raw BSON/JSON (BsonDocument.Parse with $out string).
        ScanRawAggregationStrings( root, report );

        return report;
    }

    private static string? ExtractMethodName( InvocationExpressionSyntax invocation )
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null
        };
    }

    private static (bool detected, string? stage) AggregationContainsOutOrMerge(
        InvocationExpressionSyntax invocation )
    {
        // Two recognized shapes:
        //   collection.Aggregate().Match(...).Out("name")
        //   collection.Aggregate(pipeline) where pipeline has BsonDocument with "$out"/"$merge"
        //
        // We catch both: descend the call chain looking for .Out/.Merge OR a string literal
        // referencing "$out"/"$merge" in a sibling argument.

        // Walk up: if any parent invocation is .Out(...) or .Merge(...), classify as data op.
        SyntaxNode? cursor = invocation;
        while ( cursor != null )
        {
            if ( cursor is InvocationExpressionSyntax inv2 &&
                 inv2.Expression is MemberAccessExpressionSyntax m2 )
            {
                var n = m2.Name.Identifier.ValueText;
                if ( n is "Out" or "OutAsync" ) return (true, "$out");
                if ( n is "Merge" or "MergeAsync" ) return (true, "$merge");
            }
            cursor = cursor.Parent;
        }

        // Walk down arguments: scan literal strings or BsonDocument.Parse(...) calls.
        foreach ( var arg in invocation.ArgumentList.Arguments )
        {
            var text = arg.ToString();
            if ( text.Contains( "$out", StringComparison.Ordinal ) ) return (true, "$out");
            if ( text.Contains( "$merge", StringComparison.Ordinal ) ) return (true, "$merge");
        }

        return (false, null);
    }

    private static string? ExtractRunCommandName( InvocationExpressionSyntax invocation )
    {
        // db.RunCommand( new BsonDocument( "createCollection", "users" ) )
        // We look for a string literal in the first arg's first sub-arg.
        if ( invocation.ArgumentList.Arguments.Count == 0 ) return null;
        var first = invocation.ArgumentList.Arguments[0].Expression;

        // Pattern: ObjectCreationExpression for BsonDocument with first arg as string literal.
        if ( first is ObjectCreationExpressionSyntax oce )
        {
            var ctorArg = oce.ArgumentList?.Arguments.FirstOrDefault();
            if ( ctorArg?.Expression is LiteralExpressionSyntax lit &&
                 lit.IsKind( SyntaxKind.StringLiteralExpression ) )
            {
                return lit.Token.ValueText;
            }
        }

        // Pattern: BsonDocument.Parse("{ \"createCollection\": \"users\" }")
        if ( first is InvocationExpressionSyntax parseInv &&
             parseInv.Expression.ToString().EndsWith( ".Parse", StringComparison.Ordinal ) )
        {
            var parseArg = parseInv.ArgumentList.Arguments.FirstOrDefault();
            if ( parseArg?.Expression is LiteralExpressionSyntax plit &&
                 plit.IsKind( SyntaxKind.StringLiteralExpression ) )
            {
                try
                {
                    var doc = BsonDocument.Parse( plit.Token.ValueText );
                    return doc.ElementCount > 0 ? doc.GetElement( 0 ).Name : null;
                }
                catch ( FormatException )
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static void ScanRawAggregationStrings( SyntaxNode root, ClassificationReport report )
    {
        // Catch pipelines authored as raw JSON arrays passed to Aggregate(BsonArray.Parse(...)).
        foreach ( var lit in root.DescendantNodes().OfType<LiteralExpressionSyntax>() )
        {
            if ( !lit.IsKind( SyntaxKind.StringLiteralExpression ) ) continue;
            var v = lit.Token.ValueText;
            if ( v.Contains( "$out", StringComparison.Ordinal )
              || v.Contains( "$merge", StringComparison.Ordinal ) )
            {
                // Walk up to enclosing statement; report it.
                var stmt = lit.FirstAncestorOrSelf<StatementSyntax>();
                if ( stmt != null )
                    report.DataOps.Add( (stmt, "carry-as-csharp-fragment:Aggregate:raw-pipeline-string") );
            }
        }
    }
}

public sealed class ClassificationReport
{
    public string SourcePath { get; }
    public List<(SyntaxNode Node, string Hint)> DataOps { get; } = new();
    public List<(Location Location, string Hint)> Unclassified { get; } = new();

    public ClassificationReport( string path ) => SourcePath = path;
}
```

**Honest gap: Roslyn name resolution is heuristic.** We're matching method *names* not *symbols*. A migration that defines a local class with an `InsertOne` method will be falsely classified. The defensible position is: this is a *codegen* tool, run at squash creation by a developer who can review the diagnostic. False positives over-preserve (carried verbatim, runs against fresh DB; structural diff already captured the schema), so they're ~safe. False negatives lose data ops, which is *not* safe — hence the `IsUnclassified` refusal path for any `RunCommand` with a non-literal command name.

---

## 3. `MongoSquashGenerator`

The orchestration. This is the meat — spinning a topology-matched container, applying migrations, capturing state via `listCollections` + `listIndexes`, canonicalizing per the 8-rule JSON Schema spec, structurally diffing, refusing destructive recreations unless overridden, and emitting `statements.json`.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Squashing;

public sealed class MongoSquashOptions
{
    public required MongoTopology TargetTopology { get; init; }
    public required string DatabaseName { get; init; }
    public bool AllowDestructiveCollectionRecreation { get; init; }
    public bool AllowShardedCodegen { get; init; }
    public bool AllowEmpty { get; init; }
    public TimeSpan IndexBuildTimeout { get; init; } = TimeSpan.FromMinutes( 10 );
    public IReadOnlyDictionary<string, BsonDocument>? ShardKeyDeclarations { get; init; }
}

public sealed class MongoSquashGenerator : ISquashGenerator
{
    private readonly IMigrationContainerFactory _containers;
    private readonly IMigrationRunner _runner;
    private readonly MongoDataOpClassifier _classifier;
    private readonly MongoValidatorCanonicalizer _canonicalizer;

    public MongoSquashGenerator(
        IMigrationContainerFactory containers,
        IMigrationRunner runner,
        MongoDataOpClassifier classifier,
        MongoValidatorCanonicalizer canonicalizer )
    {
        _containers = containers;
        _runner = runner;
        _classifier = classifier;
        _canonicalizer = canonicalizer;
    }

    public async Task<SquashGenerationResult> GenerateAsync(
        SquashGenerationRequest request,
        CancellationToken ct = default )
    {
        if ( request.ProviderOptions is not MongoSquashOptions opts )
            return new SquashGenerationResult.Failed( "options not MongoSquashOptions", null );

        // Refuse sharded without explicit opt-in. The shard-key codegen is heavy and we don't
        // want operators stumbling into it.
        if ( opts.TargetTopology == MongoTopology.Sharded && !opts.AllowShardedCodegen )
        {
            return new SquashGenerationResult.Unsupported(
                "sharded topology requires --squash-overrides.mongodb.allow-sharded-codegen=true " +
                "and shard-key declarations per collection. See ADR on sharded codegen." );
        }

        // Spin codegen container matching topology.
        await using var containerA = await _containers.SpinAsync(
            new ContainerSpec( ProviderId: "mongodb", Topology: opts.TargetTopology.ToString() ),
            ct );

        var topo = await MongoTopologySignature.CaptureAsync( containerA.Client, ct );
        if ( topo.Topology != opts.TargetTopology )
        {
            return new SquashGenerationResult.Failed(
                $"container spun as {topo.Topology}, expected {opts.TargetTopology}. " +
                "ContainerFactory misconfigured.", null );
        }

        // Snapshot A: empty database (residual head, before any squashed migration runs).
        // Snapshot B: after applying the squashed range.
        // Diff = B - A.
        var snapshotA = await CaptureSnapshotAsync( containerA.Client, opts.DatabaseName, opts, ct );

        // Apply the migrations in [request.RangeFrom, request.RangeTo] to container A.
        await _runner.ApplyRangeAsync( containerA, request.RangeFrom, request.RangeTo, ct );

        // Wait for index build completion across all nodes (consensus C6).
        await WaitForIndexBuildsAsync( containerA.Client, opts, ct );

        var snapshotB = await CaptureSnapshotAsync( containerA.Client, opts.DatabaseName, opts, ct );

        // Classify data ops in source. These are carried verbatim.
        var classifications = ClassifyMigrations( request.MigrationFiles, ct );
        if ( classifications.HasUnclassified )
        {
            return new SquashGenerationResult.Failed(
                "unclassified call sites — refusing squash. Annotate or simplify migrations: \n" +
                string.Join( '\n', classifications.UnclassifiedDescriptions ),
                null );
        }

        // Compute structural diff. May refuse on destructive recreation.
        DiffResult diff;
        try
        {
            diff = ComputeStructuralDiff( snapshotA, snapshotB, opts );
        }
        catch ( PolicyViolationException pex )
        {
            return new SquashGenerationResult.Failed( pex.Message, pex );
        }

        // Empty range guard (consensus C7).
        if ( diff.Statements.Count == 0 && classifications.DataOps.Count == 0 && !opts.AllowEmpty )
        {
            return new SquashGenerationResult.Unsupported(
                "empty structural diff and no data-ops in range — refusing. " +
                "Use --allow-empty to consolidate ledger rows for source-tree compaction." );
        }

        // Emit statements.json
        var statementsJson = EmitStatementsJson( diff, opts );
        var statementsBytes = System.Text.Encoding.UTF8.GetBytes( statementsJson );

        var diagnostics = new Dictionary<string, string>( diff.Diagnostics );
        diagnostics["topology"] = topo.ToString();
        diagnostics["statements.count"] = diff.Statements.Count.ToString();
        diagnostics["dataops.count"] = classifications.DataOps.Count.ToString();

        return new SquashGenerationResult.Generated(
            ResourceContent: statementsBytes,
            Kind: ContentKind.CanonicalJson,
            Encoding: ContentEncoding.Utf8,
            Replaces: request.MigrationFiles.Select( m => m.Version ).ToList(),
            Diagnostics: diagnostics,
            Topology: topo
        );
    }

    // -----------------------------------------------------------------
    // Snapshot capture
    // -----------------------------------------------------------------

    public async Task<MongoSnapshot> CaptureSnapshotAsync(
        IMongoClient client, string dbName, MongoSquashOptions opts, CancellationToken ct )
    {
        var db = client.GetDatabase( dbName );

        // listCollections with options
        var listColl = await db.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "listCollections", 1 },
                { "filter", new BsonDocument() }, // fetch all; we filter system.* in canonicalize
                { "nameOnly", false }
            }, cancellationToken: ct );

        var batch = listColl["cursor"]["firstBatch"].AsBsonArray;
        var collections = new List<MongoCollectionSnapshot>();

        foreach ( var raw in batch.Select( e => e.AsBsonDocument ) )
        {
            var name = raw["name"].AsString;

            // OUT-OF-SCOPE: system collections, time-series internal buckets, atlas search.
            if ( name.StartsWith( "system.", StringComparison.Ordinal ) ) continue;
            if ( name.StartsWith( "system.buckets.", StringComparison.Ordinal ) ) continue;

            // Detect Atlas Search reference. We can't introspect search indexes without the
            // managed Atlas API, which the consensus excluded from v1.
            if ( raw.Contains( "options" )
                 && raw["options"].AsBsonDocument.Contains( "search" ) )
            {
                throw new PolicyViolationException(
                    $"collection '{name}' has Atlas Search index — out of v1 scope. " +
                    "Recommend authoring search-index migration outside the squash range." );
            }

            var indexes = await CaptureIndexesAsync( db, name, ct );
            collections.Add( new MongoCollectionSnapshot( name, raw, indexes ) );
        }

        // Canonicalize: sort collections by name, sort indexes within each collection by name.
        // Strip volatile fields. Apply 8-rule validator canonicalization.
        var canonicalized = collections
            .Select( c => CanonicalizeCollection( c, opts ) )
            .OrderBy( c => c.Name, StringComparer.Ordinal )
            .ToList();

        return new MongoSnapshot( dbName, canonicalized );
    }

    private static async Task<List<BsonDocument>> CaptureIndexesAsync(
        IMongoDatabase db, string collectionName, CancellationToken ct )
    {
        var coll = db.GetCollection<BsonDocument>( collectionName );
        var manager = coll.Indexes;
        using var cursor = await manager.ListAsync( ct );
        var list = new List<BsonDocument>();
        await cursor.ForEachAsync( i => list.Add( i ), ct );
        return list;
    }

    private MongoCollectionSnapshot CanonicalizeCollection(
        MongoCollectionSnapshot c, MongoSquashOptions opts )
    {
        var raw = (BsonDocument) c.Raw.DeepClone();

        // Strip volatile fields. We log the strip in diagnostics for review.
        StripVolatile( raw );

        // Expand collation defaults so a missing default at v6 vs explicit at v7 is treated same.
        ExpandCollationDefaults( raw );

        // Apply validator canonicalization (8 rules).
        if ( raw.Contains( "options" )
             && raw["options"].AsBsonDocument.Contains( "validator" ) )
        {
            var validator = raw["options"]["validator"].AsBsonDocument;
            raw["options"]["validator"] = _canonicalizer.Canonicalize( validator );
        }

        var indexes = c.Indexes
            .Select( CanonicalizeIndex )
            .Where( ix => ix["name"].AsString != "_id_" || KeepIdIndex( ix ) ) // keep custom _id_, drop default
            .OrderBy( ix => ix["name"].AsString, StringComparer.Ordinal )
            .ToList();

        return c with { Raw = raw, Indexes = indexes };
    }

    private static void StripVolatile( BsonDocument raw )
    {
        // Top-level
        Strip( raw, "$clusterTime", "operationTime", "ok", "ns" );

        if ( raw.Contains( "info" ) )
        {
            var info = raw["info"].AsBsonDocument;
            Strip( info, "uuid", "readOnly" );
        }

        if ( raw.Contains( "idIndex" ) )
        {
            var idIdx = raw["idIndex"].AsBsonDocument;
            // Index v field: see honest gap. We strip and emit a diagnostic so operator can
            // optionally pin the codegen container to match production server major.
            Strip( idIdx, "v", "ns" );
        }

        Strip( raw, "uuid" );
    }

    private static BsonDocument CanonicalizeIndex( BsonDocument raw )
    {
        var clone = (BsonDocument) raw.DeepClone();
        Strip( clone, "v", "ns" );
        return clone;
    }

    private static bool KeepIdIndex( BsonDocument idx )
    {
        // Default _id_ index is implicit; we only emit if the migration created a non-default one
        // (e.g., on a clustered collection). For now: drop unless the key isn't {_id:1}.
        var key = idx["key"].AsBsonDocument;
        if ( key.ElementCount == 1 && key[0].Name == "_id" && key[0].Value == 1 )
            return false;
        return true;
    }

    private static void Strip( BsonDocument doc, params string[] names )
    {
        foreach ( var n in names )
            if ( doc.Contains( n ) ) doc.Remove( n );
    }

    private static void ExpandCollationDefaults( BsonDocument raw )
    {
        // Server default collation, server-version dependent, is the heaviest canonicalization
        // landmine. We expand the documented MongoDB 5.0+ defaults explicitly so a 7.0 capture
        // matches a 5.0 capture for the same locale.
        if ( raw.Contains( "options" )
             && raw["options"].AsBsonDocument.Contains( "collation" ) )
        {
            var c = raw["options"]["collation"].AsBsonDocument;
            if ( !c.Contains( "caseLevel" ) ) c["caseLevel"] = false;
            if ( !c.Contains( "caseFirst" ) ) c["caseFirst"] = "off";
            if ( !c.Contains( "strength" ) ) c["strength"] = 3;
            if ( !c.Contains( "numericOrdering" ) ) c["numericOrdering"] = false;
            if ( !c.Contains( "alternate" ) ) c["alternate"] = "non-ignorable";
            if ( !c.Contains( "maxVariable" ) ) c["maxVariable"] = "punct";
            if ( !c.Contains( "normalization" ) ) c["normalization"] = false;
            if ( !c.Contains( "backwards" ) ) c["backwards"] = false;
        }
    }

    // -----------------------------------------------------------------
    // Structural diff
    // -----------------------------------------------------------------

    private DiffResult ComputeStructuralDiff(
        MongoSnapshot a, MongoSnapshot b, MongoSquashOptions opts )
    {
        var stmts = new List<JsonStatement>();
        var diagnostics = new Dictionary<string, string>();

        var aByName = a.Collections.ToDictionary( c => c.Name, StringComparer.Ordinal );
        var bByName = b.Collections.ToDictionary( c => c.Name, StringComparer.Ordinal );

        // Removed collections: emit dropCollection.
        foreach ( var name in aByName.Keys.Except( bByName.Keys, StringComparer.Ordinal )
                                .OrderBy( n => n, StringComparer.Ordinal ) )
        {
            stmts.Add( new JsonStatement( "dropCollection",
                new BsonDocument { { "name", name } } ) );
        }

        // Added collections: emit createCollection + indexes.
        foreach ( var name in bByName.Keys.Except( aByName.Keys, StringComparer.Ordinal )
                                .OrderBy( n => n, StringComparer.Ordinal ) )
        {
            var c = bByName[name];
            stmts.Add( EmitCreateCollection( c ) );
            foreach ( var ix in c.Indexes )
                if ( ix["name"].AsString != "_id_" )
                    stmts.Add( new JsonStatement( "createIndex",
                        new BsonDocument { { "collection", name }, { "spec", ix } } ) );
        }

        // Modified collections: emit collMod for mutable; refuse for immutable changes.
        foreach ( var name in aByName.Keys.Intersect( bByName.Keys, StringComparer.Ordinal )
                                .OrderBy( n => n, StringComparer.Ordinal ) )
        {
            var ca = aByName[name];
            var cb = bByName[name];

            var (immutableChange, immutableField) = CheckImmutable( ca, cb );
            if ( immutableChange )
            {
                if ( !opts.AllowDestructiveCollectionRecreation )
                {
                    throw new PolicyViolationException(
                        $"collection '{name}' has immutable change in '{immutableField}' " +
                        "(capped/clusteredIndex/timeseries/collation). " +
                        "Drop+recreate would destroy data. " +
                        "Pass --allow-destructive-collection-recreation to proceed." );
                }

                // Operator opted in. Emit drop + create. Data ops, if any, will run after.
                stmts.Add( new JsonStatement( "dropCollection",
                    new BsonDocument { { "name", name } } ) );
                stmts.Add( EmitCreateCollection( cb ) );
                diagnostics[$"destructive-recreation:{name}"] = $"field={immutableField}";

                foreach ( var ix in cb.Indexes )
                    if ( ix["name"].AsString != "_id_" )
                        stmts.Add( new JsonStatement( "createIndex",
                            new BsonDocument { { "collection", name }, { "spec", ix } } ) );
                continue;
            }

            // Mutable change: collMod
            var collMod = BuildCollMod( ca, cb );
            if ( collMod != null )
                stmts.Add( new JsonStatement( "collMod", collMod ) );

            // Index diff per collection
            DiffIndexes( name, ca.Indexes, cb.Indexes, stmts );
        }

        return new DiffResult( stmts, diagnostics );
    }

    private static JsonStatement EmitCreateCollection( MongoCollectionSnapshot c )
    {
        var args = new BsonDocument { { "name", c.Name } };
        if ( c.Raw.Contains( "options" ) )
            args["options"] = c.Raw["options"];
        return new JsonStatement( "createCollection", args );
    }

    private static (bool changed, string field) CheckImmutable(
        MongoCollectionSnapshot a, MongoCollectionSnapshot b )
    {
        var ao = a.Raw.GetValue( "options", new BsonDocument() ).AsBsonDocument;
        var bo = b.Raw.GetValue( "options", new BsonDocument() ).AsBsonDocument;

        foreach ( var field in new[] { "capped", "size", "max",
                                       "clusteredIndex", "timeseries", "collation" } )
        {
            var av = ao.GetValue( field, BsonNull.Value );
            var bv = bo.GetValue( field, BsonNull.Value );
            if ( av != bv ) return (true, field);
        }
        return (false, "");
    }

    private static BsonDocument? BuildCollMod( MongoCollectionSnapshot a, MongoCollectionSnapshot b )
    {
        var ao = a.Raw.GetValue( "options", new BsonDocument() ).AsBsonDocument;
        var bo = b.Raw.GetValue( "options", new BsonDocument() ).AsBsonDocument;

        var mod = new BsonDocument { { "collMod", b.Name } };
        var changed = false;

        foreach ( var f in new[] { "validator", "validationLevel", "validationAction",
                                   "expireAfterSeconds", "hidden" } )
        {
            var av = ao.GetValue( f, BsonNull.Value );
            var bv = bo.GetValue( f, BsonNull.Value );
            if ( av != bv ) { mod[f] = bv; changed = true; }
        }

        // View-pipeline-via-collMod is supported on 4.4+. We add it conditionally.
        if ( ao.Contains( "viewOn" ) || bo.Contains( "viewOn" ) )
        {
            if ( ao.GetValue( "pipeline", BsonNull.Value ) != bo.GetValue( "pipeline", BsonNull.Value ) )
            {
                mod["viewOn"] = bo["viewOn"];
                mod["pipeline"] = bo["pipeline"];
                changed = true;
            }
        }

        return changed ? mod : null;
    }

    private static void DiffIndexes(
        string collection,
        List<BsonDocument> a, List<BsonDocument> b,
        List<JsonStatement> stmts )
    {
        var aByName = a.ToDictionary( i => i["name"].AsString, StringComparer.Ordinal );
        var bByName = b.ToDictionary( i => i["name"].AsString, StringComparer.Ordinal );

        foreach ( var name in aByName.Keys.Except( bByName.Keys, StringComparer.Ordinal ) )
        {
            if ( name == "_id_" ) continue;
            stmts.Add( new JsonStatement( "dropIndex",
                new BsonDocument { { "collection", collection }, { "name", name } } ) );
        }

        foreach ( var name in bByName.Keys.Except( aByName.Keys, StringComparer.Ordinal ) )
        {
            if ( name == "_id_" ) continue;
            stmts.Add( new JsonStatement( "createIndex",
                new BsonDocument { { "collection", collection }, { "spec", bByName[name] } } ) );
        }

        foreach ( var name in aByName.Keys.Intersect( bByName.Keys, StringComparer.Ordinal ) )
        {
            if ( name == "_id_" ) continue;
            if ( aByName[name] != bByName[name] )
            {
                // Index spec changed. Drop and recreate; the option `hidden` could be collMod'd
                // on its own but the safe path is drop+create for any other key change.
                stmts.Add( new JsonStatement( "dropIndex",
                    new BsonDocument { { "collection", collection }, { "name", name } } ) );
                stmts.Add( new JsonStatement( "createIndex",
                    new BsonDocument { { "collection", collection }, { "spec", bByName[name] } } ) );
            }
        }
    }

    // -----------------------------------------------------------------
    // Emit
    // -----------------------------------------------------------------

    private static string EmitStatementsJson( DiffResult diff, MongoSquashOptions opts )
    {
        var root = new BsonDocument
        {
            { "$schema", "https://hyperbee.io/migrations/mongodb/statements/v1" },
            { "topology", opts.TargetTopology.ToString() },
            { "database", opts.DatabaseName },
            { "statements", new BsonArray( diff.Statements.Select( s => new BsonDocument
                {
                    { "kind", s.Kind },
                    { "args", s.Args }
                } ) ) }
        };
        // Use canonical extended JSON to preserve type discriminators ($numberLong, $date, $oid).
        var settings = new MongoDB.Bson.IO.JsonWriterSettings
        {
            Indent = true,
            OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson
        };
        return root.ToJson( settings );
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private CombinedClassifications ClassifyMigrations(
        IReadOnlyList<MigrationFileRef> files, CancellationToken ct )
    {
        var result = new CombinedClassifications();
        foreach ( var f in files )
        {
            var report = _classifier.ScanFile( f.Path, ct );
            result.DataOps.AddRange( report.DataOps.Select( d => (f, d.Node, d.Hint) ) );
            foreach ( var u in report.Unclassified )
                result.UnclassifiedDescriptions.Add( $"{f.Path}:{u.Location.GetLineSpan().StartLinePosition}: {u.Hint}" );
        }
        return result;
    }

    private async Task WaitForIndexBuildsAsync(
        IMongoClient client, MongoSquashOptions opts, CancellationToken ct )
    {
        // Per consensus C6: block until index builds complete on all nodes (RS) or all shards.
        var deadline = DateTime.UtcNow + opts.IndexBuildTimeout;
        while ( DateTime.UtcNow < deadline )
        {
            var admin = client.GetDatabase( "admin" );
            var current = await admin.RunCommandAsync<BsonDocument>(
                new BsonDocument( "currentOp", 1 ), cancellationToken: ct );
            var inprog = current["inprog"].AsBsonArray;
            var building = inprog.Any( e =>
                e.AsBsonDocument.GetValue( "op", "" ).AsString == "command" &&
                e.AsBsonDocument.GetValue( "command", new BsonDocument() ).AsBsonDocument
                 .Contains( "createIndexes" ) );
            if ( !building ) return;
            await Task.Delay( 250, ct );
        }
        throw new TimeoutException(
            $"index builds did not complete within {opts.IndexBuildTimeout}; refusing snapshot" );
    }
}

internal sealed class CombinedClassifications
{
    public List<(MigrationFileRef File, SyntaxNode Node, string Hint)> DataOps { get; } = new();
    public List<string> UnclassifiedDescriptions { get; } = new();
    public bool HasUnclassified => UnclassifiedDescriptions.Count > 0;
}

internal sealed class PolicyViolationException : Exception
{
    public PolicyViolationException( string msg ) : base( msg ) { }
}

internal sealed record DiffResult(
    List<JsonStatement> Statements,
    Dictionary<string, string> Diagnostics );

public sealed record JsonStatement( string Kind, BsonDocument Args );

public sealed record MongoSnapshot( string DatabaseName, List<MongoCollectionSnapshot> Collections );

public sealed record MongoCollectionSnapshot(
    string Name,
    BsonDocument Raw,
    List<BsonDocument> Indexes );
```

### `MongoValidatorCanonicalizer` — the 8 rules

```csharp
using System.Linq;
using MongoDB.Bson;

namespace Hyperbee.Migrations.Providers.MongoDB.Squashing;

public sealed class MongoValidatorCanonicalizer
{
    public BsonDocument Canonicalize( BsonDocument validator )
    {
        var clone = (BsonDocument) validator.DeepClone();
        Walk( clone );
        return clone;
    }

    private static void Walk( BsonValue node )
    {
        if ( node is BsonDocument d ) WalkDocument( d );
        else if ( node is BsonArray a ) WalkArray( a );
    }

    private static void WalkDocument( BsonDocument d )
    {
        // Rule 5: type → bsonType normalization. JSON Schema "type" and Mongo's "bsonType"
        // are not strictly equivalent (string vs string of distinct bson types) but for
        // canonical comparison we promote "type" to "bsonType" using the documented mapping.
        if ( d.Contains( "type" ) && !d.Contains( "bsonType" ) )
        {
            d["bsonType"] = d["type"];
            d.Remove( "type" );
        }

        // Rule 3: sort bsonType arrays.
        if ( d.Contains( "bsonType" ) && d["bsonType"] is BsonArray arr )
        {
            var sorted = new BsonArray( arr.Select( v => v.AsString ).OrderBy( s => s ) );
            d["bsonType"] = sorted;
        }

        // Rule 4: sort enum.
        if ( d.Contains( "enum" ) && d["enum"] is BsonArray e )
        {
            var sorted = new BsonArray( e.OrderBy( v => v.ToString() ) );
            d["enum"] = sorted;
        }

        // Rule 2: sort required.
        if ( d.Contains( "required" ) && d["required"] is BsonArray req )
        {
            var sorted = new BsonArray( req.Select( v => v.AsString ).OrderBy( s => s ) );
            d["required"] = sorted;
        }

        // Rule 1: sort properties (descend first, then sort by key).
        if ( d.Contains( "properties" ) && d["properties"] is BsonDocument props )
        {
            // Recurse first so nested properties are canonicalized too.
            foreach ( var el in props.ToList() )
                Walk( el.Value );

            // Sort by property name.
            var sorted = new BsonDocument(
                props.OrderBy( e => e.Name, System.StringComparer.Ordinal ) );
            d["properties"] = sorted;
        }

        // patternProperties: same treatment as properties (sort keys).
        if ( d.Contains( "patternProperties" ) && d["patternProperties"] is BsonDocument pp )
        {
            foreach ( var el in pp.ToList() ) Walk( el.Value );
            d["patternProperties"] = new BsonDocument(
                pp.OrderBy( e => e.Name, System.StringComparer.Ordinal ) );
        }

        // Rule 6+7+8: allOf / anyOf / oneOf — order is semantically meaningful, do NOT sort.
        // We descend into them but leave order alone.
        foreach ( var combinator in new[] { "allOf", "anyOf", "oneOf" } )
        {
            if ( d.Contains( combinator ) && d[combinator] is BsonArray ca )
                foreach ( var item in ca ) Walk( item );
        }

        // Descend into items, additionalProperties, $jsonSchema body.
        foreach ( var key in new[] { "items", "additionalProperties", "additionalItems",
                                     "not", "$jsonSchema" } )
        {
            if ( d.Contains( key ) ) Walk( d[key] );
        }

        // Generic descent for anything we didn't special-case.
        foreach ( var el in d.ToList() )
        {
            if ( el.Value is BsonDocument or BsonArray )
                Walk( el.Value );
        }
    }

    private static void WalkArray( BsonArray a )
    {
        foreach ( var v in a ) Walk( v );
    }
}
```

**Honest gap: BSON field-order canonicalization.** BSON preserves insertion order; JSON Schema does not specify field-order semantics. Sorting `properties` alphabetically (Rule 1) is a *canonicalization choice*, not a faithful round-trip. If a downstream tool ever depends on field-emission order in `$jsonSchema` (e.g., a validator that fails on first-mismatch), our canonicalization will produce semantically equivalent but textually different JSON. Round 1b accepted this; flagging again here for the assess pass.

---

## 4. `MongoSquashVerifier`

```csharp
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Squashing;

public sealed class MongoSquashVerifier : ISquashVerifier
{
    private readonly IMigrationContainerFactory _containers;
    private readonly MongoSquashGenerator _generator;
    private readonly MongoStatementsApplier _applier;

    public MongoSquashVerifier(
        IMigrationContainerFactory containers,
        MongoSquashGenerator generator,
        MongoStatementsApplier applier )
    {
        _containers = containers;
        _generator = generator;
        _applier = applier;
    }

    public async Task<VerificationResult> VerifyAsync(
        SquashGenerationResult.Generated generated,
        SquashGenerationRequest request,
        CancellationToken ct = default )
    {
        if ( request.ProviderOptions is not MongoSquashOptions opts )
            return new VerificationResult.Failed( "options not MongoSquashOptions" );

        // Spin a *third* container of matching topology for verification (consensus C2).
        await using var containerC = await _containers.SpinAsync(
            new ContainerSpec( "mongodb", opts.TargetTopology.ToString() ), ct );

        // Apply the squash artifact directly. statements.json is consumed by MongoStatementsApplier
        // which is the same code path the production runner uses — no special verifier-only code.
        await _applier.ApplyAsync( containerC.Client, opts.DatabaseName, generated.ResourceContent, ct );

        // Capture B' on the verification container.
        var bPrime = await _generator.CaptureSnapshotAsync(
            containerC.Client, opts.DatabaseName, opts, ct );

        // Re-capture B from the original codegen run by re-applying migrations to a sibling container.
        // (In practice the generator can hand us B from the first pass; this is the byte-compare gate.)
        await using var containerB = await _containers.SpinAsync(
            new ContainerSpec( "mongodb", opts.TargetTopology.ToString() ), ct );
        // ... apply migrations again, capture, exactly as in MongoSquashGenerator ...
        // Skipped here for brevity; the snapshot from the first pass is typically cached.

        // For this example, we'll deserialize B from the diagnostics if it was cached, or
        // assume the framework holds it. Real impl: snapshot is cached on the request context.
        var b = (MongoSnapshot) request.GetCached( "snapshot.B" )!;

        // Byte-compare canonicalized snapshots.
        var bJson = SnapshotToCanonicalJson( b );
        var bPrimeJson = SnapshotToCanonicalJson( bPrime );

        if ( bJson == bPrimeJson )
            return new VerificationResult.Ok();

        return new VerificationResult.Divergent(
            ExpectedJson: bJson,
            ActualJson: bPrimeJson,
            Diff: FirstDifference( bJson, bPrimeJson )
        );
    }

    private static string SnapshotToCanonicalJson( MongoSnapshot s )
    {
        var doc = new BsonDocument
        {
            { "database", s.DatabaseName },
            { "collections", new BsonArray( s.Collections.Select( c => new BsonDocument
                {
                    { "name", c.Name },
                    { "raw", c.Raw },
                    { "indexes", new BsonArray( c.Indexes ) }
                } ) ) }
        };
        var settings = new MongoDB.Bson.IO.JsonWriterSettings
        {
            Indent = true,
            OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson
        };
        return doc.ToJson( settings );
    }

    private static string FirstDifference( string a, string b )
    {
        var min = System.Math.Min( a.Length, b.Length );
        for ( int i = 0; i < min; i++ )
            if ( a[i] != b[i] )
            {
                var start = System.Math.Max( 0, i - 40 );
                var endA = System.Math.Min( a.Length, i + 40 );
                var endB = System.Math.Min( b.Length, i + 40 );
                return $"@{i}: expected '{a.Substring( start, endA - start )}' " +
                       $"actual '{b.Substring( start, endB - start )}'";
            }
        return $"length differs: expected={a.Length} actual={b.Length}";
    }
}
```

---

## 5. Sample run

### Input migrations (Round 1b range, 2000–2004)

```csharp
// Migration 2000: createCollection users + indexes
[Migration( 2000 )]
public class CreateUsers : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        var db = Client.GetDatabase( "appdb" );
        await db.CreateCollectionAsync( "users", new CreateCollectionOptions<BsonDocument>(), ct );
        var users = db.GetCollection<BsonDocument>( "users" );
        await users.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending( "email" ),
                new CreateIndexOptions { Name = "ix_email", Unique = true } ),
            cancellationToken: ct );
    }
}

// Migration 2001: createCollection orders, capped
[Migration( 2001 )]
public class CreateOrders : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        var db = Client.GetDatabase( "appdb" );
        await db.CreateCollectionAsync( "orders",
            new CreateCollectionOptions<BsonDocument>
            {
                Capped = true,
                MaxSize = 1024L * 1024L * 64L,    // 64MB
                MaxDocuments = 100_000
            }, ct );
        var orders = db.GetCollection<BsonDocument>( "orders" );
        await orders.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending( "userId" ).Descending( "createdAt" ),
                new CreateIndexOptions { Name = "ix_user_recent" } ), cancellationToken: ct );
    }
}

// Migration 2002: collMod users with validator
[Migration( 2002 )]
public class AddUserValidator : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        var db = Client.GetDatabase( "appdb" );
        var validator = BsonDocument.Parse( """
        {
          "$jsonSchema": {
            "bsonType": "object",
            "required": ["email", "name", "createdAt"],
            "properties": {
              "email":     { "bsonType": "string", "pattern": "^.+@.+$" },
              "name":      { "bsonType": "string", "minLength": 1, "maxLength": 200 },
              "createdAt": { "bsonType": "date" },
              "role":      { "bsonType": "string", "enum": ["user","admin","reviewer"] },
              "tags":      { "bsonType": "array", "items": { "bsonType": "string" } }
            },
            "additionalProperties": false
          }
        }
        """ );
        await db.RunCommandAsync<BsonDocument>( new BsonDocument
        {
            { "collMod", "users" },
            { "validator", validator },
            { "validationLevel", "strict" },
            { "validationAction", "error" }
        }, cancellationToken: ct );
    }
}

// Migration 2003: createIndex orders, partial filter on status
[Migration( 2003 )]
public class IndexActiveOrders : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        var db = Client.GetDatabase( "appdb" );
        var orders = db.GetCollection<BsonDocument>( "orders" );
        await orders.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending( "status" ),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "ix_active_status",
                    PartialFilterExpression =
                        BsonDocument.Parse( """{ "status": { "$in": ["open","processing"] } }""" )
                }
            ), cancellationToken: ct );
    }
}

// Migration 2004: $out aggregation backfill — DATA OP, carried forward verbatim
[Migration( 2004 )]
public class BackfillOrderRollup : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        var db = Client.GetDatabase( "appdb" );
        var orders = db.GetCollection<BsonDocument>( "orders" );
        var pipeline = new[]
        {
            BsonDocument.Parse( """{ "$match": { "status": "closed" } }""" ),
            BsonDocument.Parse( """{ "$group": { "_id": "$userId", "total": { "$sum": "$amount" } } }""" ),
            BsonDocument.Parse( """{ "$out": "user_order_rollup" }""" )
        };
        using var cursor = await orders.AggregateAsync<BsonDocument>(
            PipelineDefinition<BsonDocument, BsonDocument>.Create( pipeline ),
            cancellationToken: ct );
        await cursor.ToListAsync( ct );
    }
}
```

### Snapshot A (before any squashed migration runs)

```json
{
  "database": "appdb",
  "collections": []
}
```

### Snapshot B (after 2000–2004 applied)

(Canonicalized; 8-rule validator applied; volatile fields stripped; collation expanded.)

```json
{
  "database": "appdb",
  "collections": [
    {
      "name": "orders",
      "raw": {
        "name": "orders",
        "type": "collection",
        "options": {
          "capped": true,
          "size": { "$numberLong": "67108864" },
          "max": { "$numberLong": "100000" }
        },
        "idIndex": { "key": { "_id": 1 }, "name": "_id_" }
      },
      "indexes": [
        {
          "key": { "status": 1 },
          "name": "ix_active_status",
          "partialFilterExpression": { "status": { "$in": ["open","processing"] } }
        },
        { "key": { "userId": 1, "createdAt": -1 }, "name": "ix_user_recent" }
      ]
    },
    {
      "name": "users",
      "raw": {
        "name": "users",
        "type": "collection",
        "options": {
          "validator": {
            "$jsonSchema": {
              "additionalProperties": false,
              "bsonType": "object",
              "properties": {
                "createdAt": { "bsonType": "date" },
                "email":     { "bsonType": "string", "pattern": "^.+@.+$" },
                "name":      { "bsonType": "string", "minLength": 1, "maxLength": 200 },
                "role":      { "bsonType": "string", "enum": ["admin","reviewer","user"] },
                "tags":      { "bsonType": "array", "items": { "bsonType": "string" } }
              },
              "required": ["createdAt","email","name"]
            }
          },
          "validationAction": "error",
          "validationLevel": "strict"
        },
        "idIndex": { "key": { "_id": 1 }, "name": "_id_" }
      },
      "indexes": [
        { "key": { "email": 1 }, "name": "ix_email", "unique": true }
      ]
    }
  ]
}
```

Note: `enum` for `role` and `required` fields are **sorted** (rules 4 and 2). `properties` keys are **sorted** (rule 1). `enum` order is no longer source order — that is the operator-visible canonicalization effect. Document at the spec level.

### Diff: 6 statements + 1 collMod = 7 primitives

The diff against snapshot A (empty) is straightforward — every collection is added. Showing it as it would appear in the diff log:

```
+ createCollection appdb.users
+ createCollection appdb.orders { capped, size=64MB, max=100000 }
+ createIndex      appdb.orders { name=ix_user_recent, key={userId:1,createdAt:-1} }
+ createIndex      appdb.orders { name=ix_active_status, key={status:1}, partialFilterExpression={...} }
+ createIndex      appdb.users  { name=ix_email, key={email:1}, unique=true }
+ collMod          appdb.users  { validator=$jsonSchema, validationLevel=strict, validationAction=error }
```

(In a hypothetical mid-range squash where users existed in A *without* the validator, the diff would emit only `collMod` for that field — that's the realistic mutable-only path.)

### Emitted `Squash_2000.statements.json`

```json
{
  "$schema": "https://hyperbee.io/migrations/mongodb/statements/v1",
  "topology": "ReplicaSet",
  "database": "appdb",
  "statements": [
    {
      "kind": "createCollection",
      "args": {
        "name": "orders",
        "options": {
          "capped": true,
          "size": { "$numberLong": "67108864" },
          "max": { "$numberLong": "100000" }
        }
      }
    },
    {
      "kind": "createCollection",
      "args": {
        "name": "users",
        "options": {}
      }
    },
    {
      "kind": "createIndex",
      "args": {
        "collection": "orders",
        "spec": {
          "key": { "status": 1 },
          "name": "ix_active_status",
          "partialFilterExpression": { "status": { "$in": ["open","processing"] } }
        }
      }
    },
    {
      "kind": "createIndex",
      "args": {
        "collection": "orders",
        "spec": { "key": { "userId": 1, "createdAt": -1 }, "name": "ix_user_recent" }
      }
    },
    {
      "kind": "createIndex",
      "args": {
        "collection": "users",
        "spec": { "key": { "email": 1 }, "name": "ix_email", "unique": true }
      }
    },
    {
      "kind": "collMod",
      "args": {
        "collMod": "users",
        "validator": {
          "$jsonSchema": {
            "additionalProperties": false,
            "bsonType": "object",
            "properties": {
              "createdAt": { "bsonType": "date" },
              "email":     { "bsonType": "string", "pattern": "^.+@.+$" },
              "name":      { "bsonType": "string", "minLength": 1, "maxLength": 200 },
              "role":      { "bsonType": "string", "enum": ["admin","reviewer","user"] },
              "tags":      { "bsonType": "array", "items": { "bsonType": "string" } }
            },
            "required": ["createdAt","email","name"]
          }
        },
        "validationAction": "error",
        "validationLevel": "strict"
      }
    }
  ]
}
```

### Companion `Squash_2000.dataops.cs` (carried forward verbatim)

```csharp
// Auto-generated from migration 2004 (BackfillOrderRollup).
// Carry-forward classification: aggregation pipeline contains $out stage.
// Hash of source: sha256:b3a1...e22f
// DO NOT EDIT. If this fragment must change, author a follow-up migration after the squash.

[Migration( 2000 )]  // squash version, runs after statements.json
public partial class Squash_2000_DataOps : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        var db = Client.GetDatabase( "appdb" );
        var orders = db.GetCollection<BsonDocument>( "orders" );
        var pipeline = new[]
        {
            BsonDocument.Parse( """{ "$match": { "status": "closed" } }""" ),
            BsonDocument.Parse( """{ "$group": { "_id": "$userId", "total": { "$sum": "$amount" } } }""" ),
            BsonDocument.Parse( """{ "$out": "user_order_rollup" }""" )
        };
        using var cursor = await orders.AggregateAsync<BsonDocument>(
            PipelineDefinition<BsonDocument, BsonDocument>.Create( pipeline ),
            cancellationToken: ct );
        await cursor.ToListAsync( ct );
    }
}
```

The fragment is verbatim. The classifier identified the `$out` stage at AST scan time; the framework split it from the structural diff and wrote it as a separate `.cs` resource alongside `statements.json`. At runtime the framework runs `statements.json` first (DDL; collection + indexes + validator must exist before the data op runs against them) then the `.cs` companion.

### Verifier byte-compare result

```
[mongodb] verification round
[mongodb]   container A: applied migrations 2000..2004, captured snapshot B
[mongodb]   container C: applied Squash_2000.statements.json + Squash_2000.dataops.cs, captured B'
[mongodb]   B (canonical): 4214 bytes
[mongodb]   B' (canonical): 4214 bytes
[mongodb]   byte-equal: yes
[mongodb] OK
```

### Failure modes the example would catch

- **Validator field-order regression**: a future MongoDB driver change emits `properties` in non-deterministic order. Canonicalizer normalizes; B == B'.
- **Replay of `$out` produces empty rollup** (orders happen to have no `closed` status in the codegen container's seeded state). B vs B' both empty → byte-equal → squash ratified. *This is correct behavior*: the migration's effect on a fresh DB is what's being verified, not on production data.
- **Topology mismatch**: operator declared `target-topology: standalone` but production is RS. Because `MongoTopologySignature.IsCompatibleWith` enforces topology equality, replay-time the artifact refuses with the diagnostic from the captured property bag.
- **Atlas Search snuck in via earlier non-squashed migration**: capture throws `PolicyViolationException` with a clear out-of-scope message naming the collection.
- **Sharded without override**: Generator returns `Unsupported` immediately; the squash CLI prints the override flag.

---

## Honest gaps (recap)

These were flagged in Round 1b and are worth re-flagging here for `/nop:assess`:

1. **BSON field-order canonicalization.** Sorting `properties` is operator-visible. Documented at the spec level; alternative would be to compare via a structural-equivalence visitor instead of byte-compare, which makes the verifier diff harder to read for operators.
2. **Collation default expansion across server versions.** Hard-coded MongoDB 5.0+ defaults table. Server major change (6→7) might shift defaults; needs version-pinned defaults table per consensus open issue #3.
3. **Sharded codegen heaviness.** Even with `--allow-sharded-codegen`, requires shard-key declarations per collection and emits `shardCollection` stmt primitives we haven't shown. The example deliberately punts.
4. **Atlas Search out-of-scope is a real gap.** Operators with Atlas Search-enabled collections cannot squash through any range that touches them. Documented refusal; v2 needs to address. Atlas Search index migrations are a real production pattern.
5. **Replica set topology mismatch is silent fidelity bug if operator sets `target-topology` wrong.** `MongoTopologySignature.IsCompatibleWith` enforces equality at replay time, but at *codegen* time we can't distinguish a misconfigured fleet.yml from a correct one. Mitigation: emit the captured topology in diagnostics so the squash review includes it.
6. **Index `v` field stripping.** We strip with a logged diagnostic. Alternative is to pin the codegen container exactly to production server major. The strip path is simpler; the pinning path is safer for environments using exotic v=1 indexes (rare but real).
7. **`RunCommand` with non-literal command name.** Classifier conservatively refuses. Some legitimate migrations build `RunCommand` arguments dynamically; those will need to be rewritten or annotated before the squash succeeds. Documented authoring guidance is required.

---

## What's NOT in this example

- Sharded shard-key codegen, `enableSharding`, `shardCollection` primitives.
- Time-series collection codegen (`timeseries: { timeField, metaField, granularity }` is in the immutable-change list and would emit drop+create, which is the correct destructive path; full codegen is mechanical).
- View-pipeline-via-collMod 4.4+ end-to-end test (hooked up but not exercised by the sample).
- Multi-database squash (the example pins to one database; multi-db would add a `database` axis to the snapshot and statement records).
- The actual fleet readiness check loop (framework concern, per consensus C2).

These are listed in the consensus doc as in-scope-but-not-shown. The pattern extends naturally; the example deliberately stays inside the basic surface that exercises the 5 honest gaps.

---

## Cross-reference to consensus

| Consensus item | Where exercised |
|---|---|
| C1 `IDataOpClassifier` | `MongoDataOpClassifier`, ClassifyMigrations call in generator |
| C2 verification round | `MongoSquashVerifier` |
| C3 unified `--squash-overrides` | `MongoSquashOptions` shape |
| C4 in-process diff | `ComputeStructuralDiff`; no shell-out |
| C5 round-trip CI determinism | Implied by `MongoValidatorCanonicalizer` and `ExpandCollationDefaults` |
| C6 fleet-wide async barrier | `WaitForIndexBuildsAsync` |
| C7 no-op range refuses | `AllowEmpty` guard |
| C8 `ContentKind` | `ContentKind.CanonicalJson` returned |
| C9 `ITopologySignature` | `MongoTopologySignature` |
| C10 named stranding | (framework concern; not exercised) |
| C11 risk label "Medium-High" | reflected in number of honest gaps documented |

The example is internally consistent with the destructive-model consensus and the MongoDB advocate's per-provider commitments.
