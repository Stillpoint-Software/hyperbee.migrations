namespace Hyperbee.Migrations.Helper;

/// <summary>
/// Parses the <see cref="MigrationAttribute.ReplacesRange"/> shorthand syntax
/// into a sorted, deduplicated set of version numbers (per ADR-0019).
/// </summary>
/// <remarks>
/// Grammar: comma-separated terms; each term is either a single integer
/// (<c>1700</c>) or an inclusive <c>start-end</c> range (<c>1000-1500</c>).
/// Whitespace is ignored.
/// <para>
/// Resolution against the assembly's actual <see cref="MigrationAttribute"/>
/// version set happens at the call site (Phase 2 Task 2.3); this helper only
/// expands the textual form.
/// </para>
/// </remarks>
internal static class ReplacesRangeParser
{
    public static SortedSet<long> Parse( string input )
    {
        var result = new SortedSet<long>();
        if ( string.IsNullOrWhiteSpace( input ) )
            return result;

        var terms = input.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        foreach ( var term in terms )
        {
            var dash = term.IndexOf( '-' );
            if ( dash < 0 )
            {
                if ( !long.TryParse( term, out var single ) )
                    throw new FormatException(
                        $"Invalid term '{term}' in ReplacesRange '{input}'. " +
                        "Expected a single integer or 'start-end' range." );
                result.Add( single );
                continue;
            }

            var leftText = term[..dash].Trim();
            var rightText = term[(dash + 1)..].Trim();

            if ( !long.TryParse( leftText, out var start ) || !long.TryParse( rightText, out var end ) )
                throw new FormatException(
                    $"Invalid range '{term}' in ReplacesRange '{input}'. " +
                    "Expected 'start-end' with integer endpoints." );

            if ( end < start )
                throw new FormatException(
                    $"Invalid range '{term}' in ReplacesRange '{input}': end ({end}) is less than start ({start})." );

            for ( var v = start; v <= end; v++ )
                result.Add( v );
        }

        return result;
    }
}
