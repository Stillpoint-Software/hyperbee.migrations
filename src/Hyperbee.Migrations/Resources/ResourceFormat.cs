namespace Hyperbee.Migrations.Resources;

/// <summary>
/// Resource-file content format. Per ADR-0022 the framework supports both the
/// legacy JSON-array container (<c>.statements.json</c>) and the universal
/// script form (<c>.statements</c> for the four NoSQL providers, <c>.sql</c>
/// for Postgres). Both forms produce the same AST stream into a provider's
/// dispatcher; the loader chooses by extension.
/// </summary>
public enum ResourceFormat : byte
{
    /// <summary>
    /// Legacy JSON-array container — `.statements.json`. Backward-compatible
    /// with v2 resource migrations.
    /// </summary>
    JsonArray = 0,

    /// <summary>
    /// Universal script form — `.statements` (NoSQL providers) or `.sql`
    /// (Postgres native). Multi-statement bodies separated by `;` with
    /// `--`/`//`/`/* */` comments per the per-provider grammar.
    /// </summary>
    Script = 1
}
