// <copyright file="UploadKeyComparer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;

namespace CSUploader.ViewModels;

/// <summary>
/// Ranks two column values for the Uploads tab's sort.
/// <para>
/// This compares KEYS, not rows. The tree shape is produced structurally by
/// <see cref="UploadRowOrder"/> — a package row is emitted immediately before its own files — so
/// nothing here has to know about packages, tiers or tiebreakers, and the ordering hazards a row
/// comparer carries (an intransitive tier rule, an overflowing subtraction, a tiebreaker that
/// turns out not to be unique) cannot arise.
/// </para>
/// </summary>
/// <param name="direction">Which way non-null values rank.</param>
internal sealed class UploadKeyComparer(ListSortDirection direction) : IComparer<object?>
{
    /// <summary>
    /// Ranks two values, with unknown values (null) sorted last in BOTH directions.
    /// <para>
    /// That asymmetry is deliberate: descending is therefore not the exact reverse of ascending.
    /// On an idle queue most rows have no Speed, ETA or Finished time, and burying the grid's
    /// real content under a wall of blanks whenever the direction flips is worse than the
    /// inconsistency. Spreadsheets settle it the same way.
    /// </para>
    /// </summary>
    public int Compare(object? x, object? y)
    {
        if (x is null)
        {
            return y is null ? 0 : 1;
        }

        if (y is null)
        {
            return -1;
        }

        // Normalised to a sign before any negation: a well-behaved IComparable may return
        // int.MinValue for "less than", and -int.MinValue is int.MinValue again — which would make
        // Compare(x,y) and Compare(y,x) BOTH negative and destroy the ordering.
        int result = Math.Sign(CompareValues(x, y));
        return direction == ListSortDirection.Descending ? -result : result;
    }

    private static int CompareValues(object x, object y)
    {
        // Display strings — hoster, account, file name — rank the way the reader's language
        // orders them, the app being localized. Case is noise here, never intent.
        if (x is string left && y is string right)
        {
            return string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
        }

        // Same-typed comparables cover every current column: sizes and speeds as long?, the
        // timestamps as DateTime?, Status by its FileState order (lifecycle, not localized
        // spelling), Progress as double?. Nullable<T> boxes to T, so a long?/long pairing lands
        // here rather than in the fallback.
        if (x.GetType() == y.GetType() && x is IComparable comparable)
        {
            return comparable.CompareTo(y);
        }

        // Unlike types: rank by type first, then by text within a type.
        //
        // Falling straight back to ToString() is not merely imprecise, it is CYCLIC — as ints
        // 2 < 10, as text "10" < "15", and as text "15" < "2", so 2 < 10 < 15 < 2. A comparer
        // with a cycle in it does not produce a slightly-wrong order, it produces an arbitrary
        // one. Grouping by type name first restores a total order, because the result is then
        // lexicographic on (type, text) and every component is itself totally ordered.
        //
        // Unreachable across the current 21 paths — every one of them pairs the same runtime
        // type, Nullable<T> boxing to T — and cheap insurance against the next column.
        int byType = string.CompareOrdinal(x.GetType().FullName, y.GetType().FullName);
        return byType != 0
            ? byType
            : string.Compare(x.ToString(), y.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }
}
