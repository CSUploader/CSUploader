// <copyright file="UploadRowOrder.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.ViewModels;

/// <summary>
/// Builds the Uploads tab's flat row list — packages with their files behind them — in the order
/// the grid should show it.
/// <para>
/// The ViewModel owns this ordering; no sort description is ever installed on the head's
/// collection view. That is a deliberate reversal, and the reason is recorded in
/// docs/superpowers/specs/2026-08-30-uploads-hierarchical-sort-design.md: Avalonia 12.1.2's
/// <c>DataGridCollectionView</c> validates an incremental insert against its neighbour only when
/// the insert index is below <c>Count-1</c>, and otherwise trusts the source index. Expanding a
/// package splices files in at exactly those positions, and a probe put a file ABOVE its own
/// package. Ordering the source list instead makes the tree structural rather than something a
/// comparison has to keep getting right.
/// </para>
/// </summary>
internal static class UploadRowOrder
{
    /// <summary>
    /// Projects <paramref name="packages"/> into grid rows, ranked by <paramref name="sort"/>, or
    /// in default order when it is null.
    /// <para>
    /// Each package row is emitted immediately before its own files, so "a package sits directly
    /// above its files" holds by construction in both directions — it is not an ordering rule
    /// that a descending comparison could invert.
    /// </para>
    /// <para>
    /// <c>OrderBy</c> is a STABLE sort, which is what lets every tiebreaker go: rows whose keys
    /// tie keep the order they already had, so a sort by a column most rows leave blank still
    /// reads as the queue does underneath. It also evaluates each key once per row rather than
    /// once per comparison, so values ticking on an upload thread mid-sort cannot produce an
    /// inconsistent ordering.
    /// </para>
    /// </summary>
    public static List<object> Build(IEnumerable<Package> packages, UploadSort? sort)
    {
        List<object> rows = [];
        UploadKeyComparer? comparer = sort is null ? null : new UploadKeyComparer(sort.Direction);

        IEnumerable<Package> orderedPackages = sort is null
            ? packages
            : packages.OrderBy(package => UploadRowSortKeys.KeyFor(package, sort.Path), comparer);

        foreach (Package package in orderedPackages)
        {
            rows.Add(package);

            // Collapsed packages contribute no file rows, sorted or not — the same rule the
            // incremental path follows (UploadsViewModel.AddPackageToVisibleRows).
            if (!package.IsExpanded)
            {
                continue;
            }

            IEnumerable<PackageFile> orderedFiles = sort is null
                ? package
                : package.OrderBy(file => UploadRowSortKeys.KeyFor(file, sort.Path), comparer);

            foreach (PackageFile file in orderedFiles)
            {
                rows.Add(file);
            }
        }

        return rows;
    }

    /// <summary>
    /// The files of <paramref name="package"/> in the order they should appear beneath it.
    /// </summary>
    public static List<PackageFile> OrderFiles(Package package, UploadSort? sort)
        => sort is null
            ? [.. package]
            : [.. package.OrderBy(file => UploadRowSortKeys.KeyFor(file, sort.Path), new UploadKeyComparer(sort.Direction))];

    /// <summary>
    /// Where a newly arrived package's block (its row plus its files) belongs in
    /// <paramref name="rows"/> — the index of the first package that ranks strictly after it, or
    /// the end of the list.
    /// <para>
    /// Inserting a whole block at a package-row index is what keeps this cheap AND safe. Cheap,
    /// because a package arriving mid-queue costs one positional insert rather than rebuilding
    /// and re-Resetting the entire grid, so selection and scroll survive. Safe, because the
    /// returned index is always a PACKAGE row: a block can never be dropped between some other
    /// package and its own files, whatever the ranking says.
    /// </para>
    /// <para>
    /// The ranking it produces can be slightly stale, since live values (Speed, Progress) move
    /// without the view re-sorting. That is bounded and visible only as a row sitting a little
    /// off its true rank — it can never break the tree, because adjacency here is structural
    /// rather than a consequence of the comparison.
    /// </para>
    /// </summary>
    public static int IndexForPackage(IReadOnlyList<object> rows, Package package, UploadSort? sort)
    {
        if (sort is null)
        {
            return rows.Count;
        }

        UploadKeyComparer comparer = new(sort.Direction);
        object? key = UploadRowSortKeys.KeyFor(package, sort.Path);

        for (int i = 0; i < rows.Count; i++)
        {
            // Strictly-after, so a package whose key ties joins the back of the tie — the same
            // rule stable OrderBy applies during a full rebuild.
            if (rows[i] is Package existing
                && comparer.Compare(key, UploadRowSortKeys.KeyFor(existing, sort.Path)) < 0)
            {
                return i;
            }
        }

        return rows.Count;
    }
}
