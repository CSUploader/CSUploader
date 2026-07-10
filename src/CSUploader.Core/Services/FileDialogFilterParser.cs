// <copyright file="FileDialogFilterParser.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Parses Win32 file-dialog filter strings ("Name (*.ext)|*.ext|All files (*.*)|*.*")
/// into name/pattern groups, for dialog stacks (Avalonia StorageProvider) that don't
/// speak the Win32 syntax natively. Lenient: null/empty input yields an empty list and
/// a malformed trailing name-without-patterns segment is dropped — a bad localized
/// filter string must degrade to "no filter", never crash a file dialog.
/// </summary>
public static class FileDialogFilterParser
{
    public readonly record struct FilterEntry(string Name, string[] Patterns);

    /// <summary>
    /// Splits <paramref name="filter"/> on '|' into (name, patterns) pairs; each patterns segment
    /// is split on ';', trimmed, and its empty entries dropped. An odd trailing segment (a name with
    /// no patterns) is discarded.
    /// </summary>
    public static IReadOnlyList<FilterEntry> Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [];
        }

        string[] segments = filter.Split('|');
        List<FilterEntry> entries = [];

        // Win32 filter segments come in (name, patterns) pairs. Stop before a lone trailing name
        // (odd segment count) rather than emit a pattern-less, meaningless entry.
        for (int i = 0; i + 1 < segments.Length; i += 2)
        {
            string name = segments[i].Trim();
            string[] patterns = [.. segments[i + 1]
                .Split(';')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)];
            entries.Add(new FilterEntry(name, patterns));
        }

        return entries;
    }
}
