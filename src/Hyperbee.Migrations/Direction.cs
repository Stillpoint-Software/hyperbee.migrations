using System;
using System.Collections.Generic;
using System.Linq;

namespace Hyperbee.Migrations;

public enum Direction
{
    Up,
    Down
}

public static class EnumerableExtensions
{
    internal static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>( this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Direction direction ) =>
        direction == Direction.Up
            ? source.OrderBy( keySelector )
            : source.OrderByDescending( keySelector );
}
