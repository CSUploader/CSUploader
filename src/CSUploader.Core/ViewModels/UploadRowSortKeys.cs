// <copyright file="UploadRowSortKeys.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Reflection;

namespace CSUploader.ViewModels;

/// <summary>
/// Reads the value a row is ranked on for one column of the Uploads tab. The Uploads grid holds
/// heterogeneous rows (<c>Package</c> and <c>PackageFile</c>) that expose every sort path under
/// the same name, so one reflected property name covers both — the same property
/// <see cref="ColumnValueExtractor"/> already depends on for the per-column Copy menu.
/// </summary>
internal static class UploadRowSortKeys
{
    /// <summary>
    /// Resolved property per row type and path. Sorting resolves a key once per row per sort
    /// (LINQ's <c>OrderBy</c> buffers keys rather than recomputing them per comparison), so this
    /// cache is about not re-reflecting across sorts rather than about the sort itself.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type Row, string Path), PropertyInfo?> Properties = new();

    /// <summary>
    /// Returns <paramref name="row"/>'s raw value for <paramref name="path"/>, or null when the
    /// row type has no such property.
    /// <para>
    /// <b>Raw, not formatted</b> — unlike <see cref="ColumnValueExtractor"/>, whose job is
    /// clipboard text. Formatting first would rank 1 GB below 900 MB and 09:00 below 10:00, so
    /// the value must reach the comparer as the <c>long?</c> / <c>DateTime?</c> it is.
    /// </para>
    /// <para>
    /// A null return is a real answer, not only an error path: packages have no
    /// <c>QueueOrder</c>, so sorting by the Order column ranks files inside each package and
    /// leaves the packages themselves in default order.
    /// </para>
    /// </summary>
    public static object? KeyFor(object row, string path)
    {
        PropertyInfo? property = Properties.GetOrAdd(
            (row.GetType(), path),
            static key => key.Row.GetProperty(key.Path, BindingFlags.Instance | BindingFlags.Public));

        if (property is null)
        {
            return null;
        }

        try
        {
            return property.GetValue(row);
        }
        catch (TargetInvocationException)
        {
            // Some row values are computed from collections that a half-constructed package can
            // leave empty — Package.AccountDisplay calls First() on its logins, HosterDisplay
            // indexes its hosters. Sorting is a display convenience, so a row that cannot say
            // what it is sorts as unknown (last) instead of taking the whole grid down with it.
            return null;
        }
    }
}
