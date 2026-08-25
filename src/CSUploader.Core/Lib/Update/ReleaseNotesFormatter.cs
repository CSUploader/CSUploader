// <copyright file="ReleaseNotesFormatter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text;
using System.Text.RegularExpressions;

namespace CSUploader.Lib.Update;

/// <summary>
/// Renders release-notes Markdown as the plain text a <c>TextBlock</c> can show.
/// </summary>
/// <remarks>
/// Hand-rolled for the two shapes this app actually receives — the repo's own hand-written
/// release-notes files, and GitHub's generated "What's Changed" lists — rather than a Markdown
/// dependency for a read-only pane. What it must get right, in order of how visibly each fails:
/// <list type="bullet">
/// <item>SOFT LINE BREAKS JOIN. The source files are hard-wrapped at some column; rendering the
/// wrapping as line breaks would fight the TextBlock's own wrapping and produce a ragged
/// column of half-lines.</item>
/// <item>Bullet continuations join to their bullet, for the same reason.</item>
/// <item>Inline markup — bold, emphasis, code, links — strips to its text; a pane full of
/// asterisks reads as broken, not as formatting.</item>
/// </list>
/// Not a Markdown parser: tables, nested lists and block quotes render as their raw lines, which
/// degrades to "readable but plain" rather than to garbage — acceptable for the shapes involved.
/// </remarks>
public static partial class ReleaseNotesFormatter
{
    [GeneratedRegex(@"^(?<indent>\s{0,3})(?<marker>[-*+]|\d{1,3}[.)])\s+(?<text>.*)$")]
    private static partial Regex ListItem();

    // Up to three spaces of indent, as the spec allows (a heading inside a list is still a
    // heading); the closing #-sequence strips only when whitespace precedes it, so "## C#" keeps
    // its sharp - this app's own notes are the ones most likely to say C#.
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<text>.*?)(?:\s+#+)?\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex("`(?<text>[^`]*)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*(?<text>.+?)\*\*|__(?<text2>.+?)__")]
    private static partial Regex Bold();

    // Emphasis only when the star/underscore hugs its text and, for underscores, does not sit
    // inside a word - `file_name_here` is an identifier, not italics.
    [GeneratedRegex(@"\*(?<text>[^\s*](?:[^*]*[^\s*])?)\*")]
    private static partial Regex Star();

    [GeneratedRegex(@"(?<![\w])_(?<text>[^\s_](?:[^_]*[^\s_])?)_(?![\w])")]
    private static partial Regex Underscore();

    /// <summary>
    /// The plain text, or <see langword="null"/> for input with nothing to show — so callers get
    /// one test (<c>is null</c>) instead of three ways of being blank.
    /// </summary>
    public static string? ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        List<(string Text, bool IsListItem)> blocks = [];
        StringBuilder? current = null;
        bool currentIsItem = false;

        void Close()
        {
            if (current is { Length: > 0 })
            {
                blocks.Add((current.ToString(), currentIsItem));
            }

            current = null;
        }

        foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();

            if (line.Length == 0)
            {
                Close();
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                Close();
                blocks.Add((heading.Groups["text"].Value, false));
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                Close();
                current = new StringBuilder();
                currentIsItem = true;

                // Unordered markers become a bullet; ordered ones keep their number, which reads
                // as a numbered list even in plain text.
                string marker = item.Groups["marker"].Value;
                current.Append(marker is "-" or "*" or "+" ? "• " : marker + " ");
                current.Append(item.Groups["text"].Value.Trim());
                continue;
            }

            // A continuation: joins the open block with a space (the soft line break). Without an
            // open block it starts a paragraph.
            if (current is null)
            {
                current = new StringBuilder();
                currentIsItem = false;
                current.Append(line.Trim());
            }
            else
            {
                current.Append(' ').Append(line.Trim());
            }
        }

        Close();

        StringBuilder result = new();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                // List items sit tight under one another; everything else gets a blank line.
                result.Append(blocks[i].IsListItem && blocks[i - 1].IsListItem ? "\n" : "\n\n");
            }

            result.Append(StripInline(blocks[i].Text));
        }

        string text = result.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static string StripInline(string text)
    {
        text = Image().Replace(text, m => m.Groups["alt"].Value);
        text = Link().Replace(text, m => m.Groups["text"].Value);
        text = InlineCode().Replace(text, m => m.Groups["text"].Value);
        text = Bold().Replace(text, m => m.Groups["text"].Success ? m.Groups["text"].Value : m.Groups["text2"].Value);
        text = Star().Replace(text, m => m.Groups["text"].Value);
        text = Underscore().Replace(text, m => m.Groups["text"].Value);
        return text;
    }
}
