#nullable enable
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// Per-statement classifier that delegates to <see cref="OpenSearchStatementParser"/>
/// and lifts the parser's <see cref="StatementAst"/> into a typed
/// <see cref="ClassifiedStatement"/> consumed by the snapshot strategy and verifier.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the Aerospike + Postgres reference shape but routes through the
/// existing OpenSearch grammar rather than re-implementing parsing. The
/// parser already does the heavy lifting (verb recognition, body source
/// resolution, safe-default flag computation); this classifier is a thin
/// projection from AST node to <c>(Kind, ObjectName, Body, Detail)</c>.
/// </para>
/// <para>
/// <c>WHEN VERSION</c> wrappers are classified as
/// <see cref="OpenSearchStatementKind.WhenVersion"/> and carry the wrapped
/// verb's class name in <c>Detail</c> so diagnostic messages and emitted
/// scripts can preserve the version gate alongside the wrapped operation.
/// </para>
/// <para>
/// <c>MIGRATE INDEX</c> and any other composite verbs (per
/// <see cref="CompositeStatementAst"/>) map to <see cref="OpenSearchStatementKind.Composite"/>
/// with the composite verb name in <c>ObjectName</c>. Callers that need the
/// child decomposition reparse via the underlying parser; the classifier
/// flattens to the top-level shape so the strategy's diagnostic pass treats
/// composites as a single emitted statement.
/// </para>
/// </remarks>
public sealed record ClassifiedStatement(
    OpenSearchStatementKind Kind,
    string? ObjectName,
    string Body,
    string? Detail = null );

public static class OpenSearchStatementClassifier
{
    /// <summary>
    /// Classifies a single OpenSearch statement. Returns a record with
    /// <see cref="OpenSearchStatementKind.Unknown"/> + <paramref name="statement"/>
    /// preserved as <see cref="ClassifiedStatement.Body"/> when the parser
    /// cannot consume the input.
    /// </summary>
    public static ClassifiedStatement Classify( string? statement )
    {
        if ( string.IsNullOrWhiteSpace( statement ) )
            return new ClassifiedStatement( OpenSearchStatementKind.Unknown, null, statement ?? "" );

        StatementAst ast;
        try
        {
            ast = new OpenSearchStatementParser().Parse( statement );
        }
        catch ( OpenSearchParseException ex )
        {
            return new ClassifiedStatement(
                Kind: OpenSearchStatementKind.Unknown,
                ObjectName: null,
                Body: statement,
                Detail: ex.Message );
        }

        return Project( ast, statement );
    }

    private static ClassifiedStatement Project( StatementAst ast, string body )
    {
        return ast switch
        {
            CreateIndexAst c => new ClassifiedStatement( OpenSearchStatementKind.CreateIndex, c.IndexName, body ),
            DropIndexAst c => new ClassifiedStatement( OpenSearchStatementKind.DropIndex, c.IndexName, body ),
            UpdateMappingAst c => new ClassifiedStatement( OpenSearchStatementKind.UpdateMapping, c.IndexName, body ),
            UpdateSettingsAst c => new ClassifiedStatement( OpenSearchStatementKind.UpdateSettings, c.IndexName, body ),
            RefreshAst c => new ClassifiedStatement( OpenSearchStatementKind.Refresh, c.IndexName, body ),
            WaitForHealthAst c => new ClassifiedStatement( OpenSearchStatementKind.WaitForHealth, c.IndexName, body ),
            WaitUntilTaskAst c => new ClassifiedStatement( OpenSearchStatementKind.WaitUntilTask, c.TaskId, body ),
            ReindexAst c => new ClassifiedStatement( OpenSearchStatementKind.Reindex, c.Destination, body ),
            AliasSwapAst c => new ClassifiedStatement( OpenSearchStatementKind.AliasSwap, c.Alias, body ),
            AliasAddAst c => new ClassifiedStatement( OpenSearchStatementKind.AliasAdd, c.Alias, body ),
            AliasRemoveAst c => new ClassifiedStatement( OpenSearchStatementKind.AliasRemove, c.Alias, body ),
            CreateTemplateAst c => new ClassifiedStatement( OpenSearchStatementKind.CreateTemplate, c.TemplateName, body ),
            DropTemplateAst c => new ClassifiedStatement( OpenSearchStatementKind.DropTemplate, c.TemplateName, body ),
            CreateComponentAst c => new ClassifiedStatement( OpenSearchStatementKind.CreateComponent, c.ComponentName, body ),
            DropComponentAst c => new ClassifiedStatement( OpenSearchStatementKind.DropComponent, c.ComponentName, body ),
            CreatePolicyAst c => new ClassifiedStatement( OpenSearchStatementKind.CreatePolicy, c.PolicyId, body ),
            ApplyPolicyAst c => new ClassifiedStatement( OpenSearchStatementKind.ApplyPolicy, c.PolicyId, body ),
            DropPolicyAst c => new ClassifiedStatement( OpenSearchStatementKind.DropPolicy, c.PolicyId, body ),
            DetachPolicyAst c => new ClassifiedStatement( OpenSearchStatementKind.DetachPolicy, c.IndexPattern, body ),
            CompositeStatementAst c => new ClassifiedStatement(
                Kind: OpenSearchStatementKind.Composite,
                ObjectName: c.CompositeVerb,
                Body: body,
                Detail: $"composite of {c.Children.Length} child statement(s): {string.Join( "; ", c.Children.Select( x => x.Verb ) )}" ),
            WhenVersionAst c =>
                // Walk through the wrapper but carry the version gate in Detail
                // so emitted diagnostics preserve the gate condition.
                Project( c.Child, body ) with
                {
                    Kind = OpenSearchStatementKind.WhenVersion,
                    Detail = $"gated by WHEN VERSION {c.Op} '{c.Version}'; wrapped verb: {c.Child.Verb}"
                },
            _ => new ClassifiedStatement(
                Kind: OpenSearchStatementKind.Unknown,
                ObjectName: null,
                Body: body,
                Detail: $"AST node {ast.GetType().Name} has no classifier projection" )
        };
    }
}
