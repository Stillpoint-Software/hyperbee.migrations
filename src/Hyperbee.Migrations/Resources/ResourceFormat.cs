namespace Hyperbee.Migrations.Resources;

/// <summary>
/// Resource-file content format. The framework supports the recommended
/// multi-statement script form (<c>.pql</c> for all providers; <c>.sql</c>
/// is the Postgres-native equivalent) and the legacy v2 JSON-array container
/// (<c>.statements.json</c>) for backward compatibility. Both forms produce
/// the same AST stream into a provider's dispatcher; the loader chooses by
/// extension.
/// </summary>
public enum ResourceFormat : byte
{
    /// <summary>
    /// Legacy JSON-array container — `.statements.json`. Backward-compatible
    /// with v2 resource migrations; not the recommended form for new work.
    /// </summary>
    JsonArray = 0,

    /// <summary>
    /// Recommended script form — `.pql` (all providers) or `.sql`
    /// (Postgres native). Multi-statement bodies separated by `;` with
    /// `--`/`//`/`/* */` comments per the per-provider grammar.
    /// </summary>
    Script = 1
}
