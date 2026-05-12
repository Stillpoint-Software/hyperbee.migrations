#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// OpenSearch statement kinds recognized by <see cref="OpenSearchStatementClassifier"/>.
/// Mirrors the AST node taxonomy in <c>Internal/Ast/</c> with an additional
/// <see cref="Unknown"/> default-deny state for inputs the parser cannot consume.
/// </summary>
/// <remarks>
/// The <see cref="Composite"/> kind represents <c>MIGRATE INDEX</c> and any
/// future multi-child composite verbs the grammar adds. The
/// <see cref="WhenVersion"/> kind represents a <c>WHEN VERSION ...</c> wrapper;
/// callers walk through to <c>Child</c> on the underlying AST when they need
/// the wrapped verb's classification.
/// </remarks>
public enum OpenSearchStatementKind : byte
{
    Unknown = 0,
    CreateIndex,
    DropIndex,
    UpdateMapping,
    UpdateSettings,
    Refresh,
    WaitForHealth,
    WaitUntilTask,
    Reindex,
    AliasSwap,
    AliasAdd,
    AliasRemove,
    CreateTemplate,
    DropTemplate,
    CreateComponent,
    DropComponent,
    CreatePolicy,
    ApplyPolicy,
    DropPolicy,
    DetachPolicy,
    Composite,
    WhenVersion
}
