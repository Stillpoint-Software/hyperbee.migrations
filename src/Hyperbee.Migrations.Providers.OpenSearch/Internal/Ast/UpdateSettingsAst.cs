#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// UPDATE SETTINGS ON <idx> [CLOSE] WITH BODY $body
//
// CLOSE flag opts into the close → update → open dance required for STATIC
// settings (number_of_shards, codec, analysis chain, store type per R-08a).
// Without CLOSE, the middleware sends `PUT /<idx>/_settings` directly and
// the cluster will reject any static-setting changes. Authors who need
// static updates must explicitly write CLOSE to acknowledge the brief
// write-unavailability window.

public sealed record UpdateSettingsAst(
    string IndexName,
    bool Close,
    BodySource? Body,
    string? NoWaitJustification = null
) : StatementAst
{
    public override string Verb => "UPDATE SETTINGS";
}
