// <copyright file="UploadSort.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;

namespace CSUploader.ViewModels;

/// <summary>
/// The Uploads tab's active sort: which column, which way. A null <c>UploadSort</c> anywhere in
/// this feature means "no sort" — the grid's default order — rather than a third enum state.
/// </summary>
/// <param name="Path">
/// The row property the column ranks on, which is exactly the column's <c>SortMemberPath</c> in
/// <c>UploadsView.axaml</c>. Both row types expose these under the same name (the same fact
/// <see cref="ColumnValueExtractor"/> relies on), so one path serves packages and files alike.
/// </param>
/// <param name="Direction">Which way that column is ranked.</param>
public sealed record UploadSort(string Path, ListSortDirection Direction)
{
    private const char Separator = '|';
    private const string Ascending = "asc";
    private const string Descending = "desc";

    /// <summary>
    /// The persisted form, <c>path|asc</c> or <c>path|desc</c>. Stored in its own Setting row
    /// rather than inside the column-state row, so a sort can be cleared without rewriting the
    /// column widths and visibility beside it.
    /// </summary>
    public string Format()
        => Path + Separator + (Direction == ListSortDirection.Descending ? Descending : Ascending);

    /// <summary>
    /// Reads back <see cref="Format"/>. Anything unreadable — blank, malformed, or a direction
    /// word we do not know — yields false and therefore default order, matching how the column
    /// state treats an entry it cannot match. A persisted sort is a convenience; none of it is
    /// worth failing a startup over.
    /// </summary>
    /// <remarks>
    /// Whether the named column still EXISTS is not checked here: this type knows nothing about
    /// the grid. The head drops a sort whose column it cannot find.
    /// </remarks>
    public static bool TryParse(string? value, out UploadSort? sort)
    {
        sort = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(Separator);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return false;
        }

        ListSortDirection? direction = parts[1].Trim().ToLowerInvariant() switch
        {
            Ascending => ListSortDirection.Ascending,
            Descending => ListSortDirection.Descending,
            _ => null,
        };

        if (direction is null)
        {
            return false;
        }

        sort = new UploadSort(parts[0].Trim(), direction.Value);
        return true;
    }
}
