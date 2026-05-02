#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// WHEN VERSION <op> '<version>' <statement>
//
// Statement-level prefix (R-15a) that gates execution of the wrapped child
// statement on the live cluster's reported version.
//
// Per ADR-0015 the parser is offline-pure: the version literal is parsed to a
// System.Version at parse time so unparseable inputs fail fast; the cluster's
// version is fetched at dispatch time via `GET /` and cached for the lifetime
// of the dispatcher.
//
// v1 supports the canonical MAJOR.MINOR[.PATCH] form. `-SNAPSHOT`, `-rc<N>`,
// and AWS `OpenSearch_<x>` suffix/prefix handling is deferred (see the
// requirements doc Open Questions section); unrecognized version literals are
// rejected at parse time with a remediation message so the failure mode is
// loud rather than silent-wrong.

public enum VersionComparator
{
    Eq,
    NotEq,
    Lt,
    LtEq,
    Gt,
    GtEq
}

public sealed record WhenVersionAst(
    VersionComparator Op,
    Version Version,
    StatementAst Child
) : StatementAst
{
    public override string Verb => $"WHEN VERSION ({Child.Verb})";

    public bool Evaluate( Version clusterVersion )
    {
        ArgumentNullException.ThrowIfNull( clusterVersion );

        // Normalize both sides so `2.10` (Major=2, Minor=10, Build=-1, Revision=-1)
        // compares cleanly to `2.10.0` (Build=0). System.Version's default
        // CompareTo distinguishes -1 from 0 as "version unspecified" — we want
        // missing components to compare equal to zeroed components per R-15a
        // metric `'2.10.0' = '2.10'`.
        var lhs = Normalize( clusterVersion );
        var rhs = Normalize( Version );

        var cmp = lhs.CompareTo( rhs );

        return Op switch
        {
            VersionComparator.Eq => cmp == 0,
            VersionComparator.NotEq => cmp != 0,
            VersionComparator.Lt => cmp < 0,
            VersionComparator.LtEq => cmp <= 0,
            VersionComparator.Gt => cmp > 0,
            VersionComparator.GtEq => cmp >= 0,
            _ => throw new InvalidOperationException( $"Unknown comparator: {Op}." )
        };
    }

    private static Version Normalize( Version v )
    {
        var build = v.Build < 0 ? 0 : v.Build;
        var revision = v.Revision < 0 ? 0 : v.Revision;
        return new Version( v.Major, v.Minor, build, revision );
    }
}
