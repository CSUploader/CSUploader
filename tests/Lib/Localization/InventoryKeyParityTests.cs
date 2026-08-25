// <copyright file="InventoryKeyParityTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Text.RegularExpressions;

namespace CSUploader.Tests.Lib.Localization;

/// <summary>
/// The translated inventories and the English one hold the same keys, and each translation is
/// substitutable for its English value.
/// <para>
/// <c>md-to-resx.py --check</c> gates each file against its OWN regeneration, so a key added to
/// <c>i18n-inventory.md</c> and forgotten in the other five passes every existing gate — and the
/// missing string surfaces as a raw key in the UI for those five languages, at runtime, in a build
/// nobody flagged. This is the gate that compares them to each other.
/// </para>
/// <para>
/// STRICT, with no allowlist. There used to be an <c>UntranslatedBacklog</c> set naming 51 keys
/// that predated the gate; they have been translated and the mechanism is deliberately gone rather
/// than left empty, because an empty escape hatch is one edit away from being an escape hatch
/// again. If a future change truly must ship a key untranslated, the English text in the translated
/// file satisfies this gate — that is the pressure valve, and it keeps the string rendering.
/// </para>
/// </summary>
public class InventoryKeyParityTests
{
    private static readonly Regex Entry = new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*)$");

    /// <summary>The generator's own comment rule (md-to-resx.py INLINE_COMMENT_RE), mirrored
    /// exactly: an audit that strips comments differently audits values the app never renders —
    /// and the comments repeat the placeholders ("# {0} = file count"), so a looser rule
    /// double-counts them.</summary>
    private static readonly Regex InlineComment = new(@"\s+#\s.*$");

    private static readonly Regex Placeholder = new(@"\{\d+\}");

    public static readonly TheoryData<string> TranslatedCultures = ["zh-Hans", "ja", "ko", "vi", "fil"];

    private static string InventoryDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "i18n-inventory.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "docs");
    }

    /// <summary>Key → rendered value inside the fenced blocks — the only lines the generator
    /// reads, stripped the way the generator strips them.</summary>
    private static Dictionary<string, string> EntriesOf(string path)
    {
        Dictionary<string, string> entries = [];
        bool inFence = false;

        foreach (string line in File.ReadAllText(path).Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence && Entry.Match(line.TrimEnd('\r')) is { Success: true } m)
            {
                entries[m.Groups["key"].Value] = InlineComment.Replace(m.Groups["value"].Value.TrimEnd(), string.Empty).TrimEnd();
            }
        }

        return entries;
    }

    [Theory]
    [MemberData(nameof(TranslatedCultures))]
    public void EveryTranslatedInventory_HoldsTheSameKeysAsEnglish(string culture)
    {
        string docs = InventoryDirectory();
        HashSet<string> english = [.. EntriesOf(Path.Combine(docs, "i18n-inventory.md")).Keys];
        HashSet<string> translated = [.. EntriesOf(Path.Combine(docs, $"i18n-inventory.{culture}.md")).Keys];

        Assert.NotEmpty(english);

        string[] missing = [.. english.Except(translated).Order()];
        Assert.True(
            missing.Length == 0,
            $"i18n-inventory.{culture}.md is missing {missing.Length} key(s) the English inventory has: "
            + string.Join(", ", missing.Take(20))
            + ". Add them there (the English text is fine for an untranslated string) and regenerate "
            + "that culture's resx with scripts/md-to-resx.py.");

        // The gate has to run in BOTH directions, or a key removed from English but left in the
        // translations goes unnoticed and each culture keeps shipping a string nothing renders.
        string[] orphaned = [.. translated.Except(english).Order()];
        Assert.True(
            orphaned.Length == 0,
            $"i18n-inventory.{culture}.md has {orphaned.Length} key(s) the English inventory does not: "
            + string.Join(", ", orphaned.Take(20)) + ". Remove them, or restore them to English.");
    }

    /// <summary>
    /// A translation must be SUBSTITUTABLE for its English value: the same format placeholders, the
    /// same number of literal <c>\n</c> line breaks.
    /// </summary>
    /// <remarks>
    /// Key presence cannot catch either failure. A translation that loses <c>{1}</c> renders a
    /// sentence with a hole in it; one that gains <c>{2}</c> makes <c>string.Format</c> THROW at
    /// the call site, at runtime, only in that language — the worst possible place to find out. A
    /// multiset comparison, not a set: "{0} of {0}" and "{0}" both reduce to the same set, and only
    /// one of them repeats the value the way the English string promises. Reordering placeholders
    /// is fine and languages genuinely need it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TranslatedCultures))]
    public void EveryTranslation_KeepsItsPlaceholdersAndLineBreaks(string culture)
    {
        string docs = InventoryDirectory();
        Dictionary<string, string> english = EntriesOf(Path.Combine(docs, "i18n-inventory.md"));
        Dictionary<string, string> translated = EntriesOf(Path.Combine(docs, $"i18n-inventory.{culture}.md"));

        List<string> broken = [];
        foreach ((string key, string en) in english)
        {
            if (!translated.TryGetValue(key, out string? value))
            {
                continue; // key parity is the other test's finding; one defect, one failure
            }

            string[] want = [.. Placeholder.Matches(en).Select(m => m.Value).Order()];
            string[] got = [.. Placeholder.Matches(value).Select(m => m.Value).Order()];
            if (!want.SequenceEqual(got))
            {
                broken.Add($"{key}: placeholders [{string.Join(" ", want)}] vs [{string.Join(" ", got)}]");
            }
            else if (CountOf(en, "\\n") != CountOf(value, "\\n"))
            {
                broken.Add($"{key}: {CountOf(en, "\\n")} literal \\n vs {CountOf(value, "\\n")}");
            }
        }

        Assert.True(
            broken.Count == 0,
            $"i18n-inventory.{culture}.md has {broken.Count} value(s) not substitutable for English: "
            + string.Join("; ", broken.Take(10)));
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
