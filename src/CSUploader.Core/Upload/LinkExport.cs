// <copyright file="LinkExport.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text;

namespace CSUploader.Upload;

/// <summary>How <see cref="LinkExport.Format"/> groups the links: one block per file (its mirrors
/// across hosters) or one block per hoster (its links across files).</summary>
public enum LinkExportGroupBy
{
    File,
    Hoster,
}

/// <summary>The markup dialect of the exported text.</summary>
public enum LinkExportFormat
{
    Plain,
    BBCode,
    Markdown,
}

/// <summary>One exportable link — a completed upload's file name, hoster, and share URL.</summary>
public readonly record struct LinkExportRow(string FileName, string HosterName, string Url);

/// <summary>
/// Paste-ready link export for the Uploads/History "Copy Links" menus: turns a package's (or
/// selection's) completed links into a text block for posting — grouped by file (mirror lists) or by
/// hoster, as plain text, BBCode, or Markdown. Framework-free and deterministic: groups appear in
/// first-seen input order, separated by a blank line, no trailing newline.
/// </summary>
public static class LinkExport
{
    /// <summary>Maps a menu item's CommandParameter key ("ByFile.Plain", "ByHoster.BBCode", …) to the
    /// export options, or null for an unknown key (the command then no-ops rather than guessing).</summary>
    public static (LinkExportGroupBy GroupBy, LinkExportFormat Format)? ParseKey(string? key) => key switch
    {
        "ByFile.Plain" => (LinkExportGroupBy.File, LinkExportFormat.Plain),
        "ByFile.BBCode" => (LinkExportGroupBy.File, LinkExportFormat.BBCode),
        "ByFile.Markdown" => (LinkExportGroupBy.File, LinkExportFormat.Markdown),
        "ByHoster.Plain" => (LinkExportGroupBy.Hoster, LinkExportFormat.Plain),
        "ByHoster.BBCode" => (LinkExportGroupBy.Hoster, LinkExportFormat.BBCode),
        "ByHoster.Markdown" => (LinkExportGroupBy.Hoster, LinkExportFormat.Markdown),
        _ => null,
    };

    /// <summary>Formats <paramref name="rows"/> per the options. Empty input yields an empty string.
    /// Plain link lines are bare URLs (the classic paste-into-post mirror block); BBCode wraps them in
    /// <c>[url]…[/url]</c> under a <c>[b]</c> header; Markdown emits <c>- [label](url)</c> bullets where
    /// the label is the grouping's counterpart (hoster name in a file block, file name in a hoster
    /// block) under a <c>**bold**</c> header.</summary>
    public static string Format(IReadOnlyList<LinkExportRow> rows, LinkExportGroupBy groupBy, LinkExportFormat format)
    {
        // Group preserving first-seen order — the input arrives in queue/selection order and the
        // exported blocks should read the same way.
        List<(string Key, List<LinkExportRow> Items)> groups = [];
        Dictionary<string, int> indexByKey = new(StringComparer.Ordinal);
        foreach (LinkExportRow row in rows)
        {
            string key = groupBy == LinkExportGroupBy.File ? row.FileName : row.HosterName;
            if (!indexByKey.TryGetValue(key, out int i))
            {
                i = groups.Count;
                indexByKey[key] = i;
                groups.Add((key, []));
            }

            groups[i].Items.Add(row);
        }

        StringBuilder sb = new();
        for (int g = 0; g < groups.Count; g++)
        {
            if (g > 0)
            {
                sb.AppendLine(); // blank line between blocks
            }

            (string key, List<LinkExportRow> items) = groups[g];
            sb.AppendLine(format switch
            {
                LinkExportFormat.BBCode => $"[b]{key}[/b]",
                LinkExportFormat.Markdown => $"**{key}**",
                _ => key,
            });

            foreach (LinkExportRow item in items)
            {
                string label = groupBy == LinkExportGroupBy.File ? item.HosterName : item.FileName;
                sb.AppendLine(format switch
                {
                    LinkExportFormat.BBCode => $"[url]{item.Url}[/url]",
                    LinkExportFormat.Markdown => $"- [{label}]({item.Url})",
                    _ => item.Url,
                });
            }
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }
}
