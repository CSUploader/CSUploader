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
    /// reads, stripped the way the generator strips them, FIRST occurrence winning the way the
    /// generator's does. Duplicates are reported, not merged: keep-last here while the generator
    /// keeps first would let a broken first value hide behind a clean duplicate and still ship.</summary>
    private static (Dictionary<string, string> Entries, List<string> Duplicates) EntriesOf(string path)
    {
        Dictionary<string, string> entries = [];
        List<string> duplicates = [];
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
                string key = m.Groups["key"].Value;
                string value = InlineComment.Replace(m.Groups["value"].Value.TrimEnd(), string.Empty).TrimEnd();
                if (!entries.TryAdd(key, value))
                {
                    duplicates.Add(key);
                }
            }
        }

        return (entries, duplicates);
    }

    /// <summary>The runtime's own ceiling on an argument index or an alignment width; at or past
    /// it, <c>string.Format</c> throws at PARSE time. Probed against .NET 10 ({0,9999999} formats,
    /// {0,10000000} throws) rather than remembered — a first probe put this at one million, which
    /// turned out to be an argument-count failure wearing a parser's clothes.</summary>
    private const int RuntimeIndexAndWidthLimit = 10_000_000;

    /// <summary>
    /// The argument indexes a composite format string consumes, or an error when it is not one.
    /// </summary>
    /// <remarks>
    /// A regex over <c>{0}</c> cannot tell <c>{0}</c> from <c>{{0}}</c> (a rendered literal) or
    /// from <c>{0} {</c> (which makes <c>string.Format</c> THROW), and misses that <c>{0:N2}</c>
    /// consumes index 0. This walks the string the way the .NET 10 parser does: <c>{{</c> and
    /// <c>}}</c> are literals; an item is an index (no leading whitespace), optional whitespace,
    /// optional <c>,alignment</c> with whitespace allowed around it, optional <c>:format</c> whose
    /// spec runs to the closing brace and may not itself contain <c>{</c> (the runtime throws on
    /// one — there is no escaping inside a spec); index and alignment respect the runtime's
    /// ten-million limit.
    /// </remarks>
    private static (List<int> Indexes, string? Error) ScanFormat(string value)
    {
        List<int> indexes = [];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '}')
            {
                if (i + 1 < value.Length && value[i + 1] == '}')
                {
                    i++;
                    continue;
                }

                return (indexes, $"stray '}}' at {i}");
            }

            if (c != '{')
            {
                continue;
            }

            if (i + 1 < value.Length && value[i + 1] == '{')
            {
                i++;
                continue;
            }

            int digitsStart = i + 1;
            int j = digitsStart;
            while (j < value.Length && char.IsAsciiDigit(value[j]))
            {
                j++;
            }

            if (j == digitsStart)
            {
                return (indexes, $"'{{' at {i} opens no argument index");
            }

            int index = int.Parse(value[digitsStart..j], System.Globalization.CultureInfo.InvariantCulture);
            if (index >= RuntimeIndexAndWidthLimit)
            {
                return (indexes, $"argument index {index} at {i} exceeds the runtime's limit");
            }

            while (j < value.Length && value[j] == ' ')
            {
                j++;
            }

            if (j < value.Length && value[j] == ',')
            {
                j++;
                while (j < value.Length && value[j] == ' ')
                {
                    j++;
                }

                if (j < value.Length && value[j] == '-')
                {
                    j++;
                }

                int alignStart = j;
                while (j < value.Length && char.IsAsciiDigit(value[j]))
                {
                    j++;
                }

                if (j == alignStart)
                {
                    return (indexes, $"malformed alignment in the item at {i}");
                }

                if (int.Parse(value[alignStart..j], System.Globalization.CultureInfo.InvariantCulture) >= RuntimeIndexAndWidthLimit)
                {
                    return (indexes, $"alignment width in the item at {i} exceeds the runtime's limit");
                }

                while (j < value.Length && value[j] == ' ')
                {
                    j++;
                }
            }

            if (j < value.Length && value[j] == ':')
            {
                j++;
                while (j < value.Length && value[j] != '}')
                {
                    if (value[j] == '{')
                    {
                        return (indexes, $"'{{' inside the format specifier of the item at {i} — the runtime throws; there is no escaping inside a spec");
                    }

                    j++;
                }
            }

            if (j >= value.Length || value[j] != '}')
            {
                return (indexes, $"unclosed format item at {i}");
            }

            indexes.Add(index);
            i = j;
        }

        indexes.Sort();
        return (indexes, null);
    }

    [Theory]
    [MemberData(nameof(TranslatedCultures))]
    public void EveryTranslatedInventory_HoldsTheSameKeysAsEnglish(string culture)
    {
        string docs = InventoryDirectory();
        (Dictionary<string, string> englishEntries, List<string> englishDupes) = EntriesOf(Path.Combine(docs, "i18n-inventory.md"));
        (Dictionary<string, string> translatedEntries, List<string> translatedDupes) = EntriesOf(Path.Combine(docs, $"i18n-inventory.{culture}.md"));
        HashSet<string> english = [.. englishEntries.Keys];
        HashSet<string> translated = [.. translatedEntries.Keys];

        Assert.NotEmpty(english);

        // A duplicated key is a defect in its own right: the generator keeps the first occurrence
        // and merely warns to stderr, where nobody is looking, and the second value silently never
        // ships. Rejected outright rather than mirrored.
        Assert.True(
            englishDupes.Count == 0,
            $"i18n-inventory.md defines {englishDupes.Count} key(s) more than once: " + string.Join(", ", englishDupes.Take(20)));
        Assert.True(
            translatedDupes.Count == 0,
            $"i18n-inventory.{culture}.md defines {translatedDupes.Count} key(s) more than once: " + string.Join(", ", translatedDupes.Take(20)));

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
        Dictionary<string, string> english = EntriesOf(Path.Combine(docs, "i18n-inventory.md")).Entries;
        Dictionary<string, string> translated = EntriesOf(Path.Combine(docs, $"i18n-inventory.{culture}.md")).Entries;

        List<string> broken = [];
        foreach ((string key, string en) in english)
        {
            if (!translated.TryGetValue(key, out string? value))
            {
                continue; // key parity is the other test's finding; one defect, one failure
            }

            (List<int> want, string? enError) = ScanFormat(en);
            (List<int> got, string? valueError) = ScanFormat(value);
            if (enError is not null)
            {
                broken.Add($"{key}: the ENGLISH value is not a valid format string ({enError})");
            }
            else if (valueError is not null)
            {
                broken.Add($"{key}: not a valid format string ({valueError}) — string.Format would throw at runtime");
            }
            else if (!want.SequenceEqual(got))
            {
                broken.Add($"{key}: consumes argument indexes [{string.Join(" ", got)}], English consumes [{string.Join(" ", want)}]");
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
