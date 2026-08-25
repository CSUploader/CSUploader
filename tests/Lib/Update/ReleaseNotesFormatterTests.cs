// <copyright file="ReleaseNotesFormatterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;

namespace CSUploader.Tests.Lib.Update;

public class ReleaseNotesFormatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public void NothingToShow_IsNull_NotAnEmptyPane(string? markdown)
        => Assert.Null(ReleaseNotesFormatter.ToPlainText(markdown));

    /// <summary>
    /// The failure that would be most visible: the source files are hard-wrapped, and rendering
    /// the wraps as line breaks fights the TextBlock's own wrapping into a ragged column.
    /// </summary>
    [Fact]
    public void SoftLineBreaks_JoinIntoOneParagraph()
    {
        string text = ReleaseNotesFormatter.ToPlainText(
            "Uploads use every connection a host allows, at the speed you\n"
            + "actually asked for. Five hosters now send parts in parallel.")!;

        Assert.DoesNotContain('\n', text);
        Assert.Contains("you actually asked", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BulletContinuations_JoinToTheirBullet()
    {
        string text = ReleaseNotesFormatter.ToPlainText(
            "- **Progress is aggregated, not per part.** The bar is one monotonic total, which is\n"
            + "  harder than it sounds when parts complete out of order.\n"
            + "- The first failure wins by part number.")!;

        string[] lines = text.Split('\n');
        Assert.Equal(2, lines.Length); // two bullets, tight — the continuation joined the first
        Assert.StartsWith("• Progress is aggregated, not per part. The bar", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("out of order.", lines[0], StringComparison.Ordinal);
        Assert.Equal("• The first failure wins by part number.", lines[1]);
    }

    [Fact]
    public void OrderedItems_KeepTheirNumbers()
    {
        string text = ReleaseNotesFormatter.ToPlainText("1. First thing\n2. Second thing")!;

        Assert.Equal("1. First thing\n2. Second thing", text);
    }

    [Fact]
    public void Headings_LoseTheirMarkersAndStandAlone()
    {
        string text = ReleaseNotesFormatter.ToPlainText("## Highlights\n\nSomething happened.")!;

        Assert.Equal("Highlights\n\nSomething happened.", text);
    }

    /// <summary>
    /// Markdown's closing-sequence rule: trailing #s strip only after whitespace. This app's own
    /// notes are the ones most likely to write a heading that ENDS in a sharp.
    /// </summary>
    [Theory]
    [InlineData("## C#", "C#")]
    [InlineData("## Heading ##", "Heading")]
    [InlineData("   ### Indented heading", "Indented heading")]  // exactly 3 spaces - the boundary the spec allows
    public void Headings_KeepTheirSharpsAndSurviveIndentation(string markdown, string expected)
        => Assert.Equal(expected, ReleaseNotesFormatter.ToPlainText(markdown));

    [Theory]
    [InlineData("**bold** and *starred* and _emphasised_", "bold and starred and emphasised")]
    [InlineData("__also bold__ text", "also bold text")]
    [InlineData("run `vpk pack` today", "run vpk pack today")]
    [InlineData("see [the docs](https://example.test/docs) for more", "see the docs for more")]
    [InlineData("![a screenshot](img.png) above", "a screenshot above")]
    public void InlineMarkup_StripsToItsText(string markdown, string expected)
        => Assert.Equal(expected, ReleaseNotesFormatter.ToPlainText(markdown));

    /// <summary>
    /// The cases stripping must NOT touch: an identifier's underscores, and arithmetic's stars.
    /// </summary>
    [Theory]
    [InlineData("the file_name_here column", "the file_name_here column")]
    [InlineData("so 2 * 3 * 4 stays what it is", "so 2 * 3 * 4 stays what it is")]
    public void ProseThatMerelyContainsMarkupCharacters_IsLeftAlone(string markdown, string expected)
        => Assert.Equal(expected, ReleaseNotesFormatter.ToPlainText(markdown));

    [Fact]
    public void BlankRuns_CollapseWithoutMergingBlocks()
    {
        string text = ReleaseNotesFormatter.ToPlainText("First paragraph.\n\n\n\nSecond paragraph.")!;

        Assert.Equal("First paragraph.\n\nSecond paragraph.", text);
    }

    /// <summary>An excerpt of the repository's own v1.5.0 notes — the primary real input.</summary>
    [Fact]
    public void TheRepositorysOwnNotes_RenderReadably()
    {
        string text = ReleaseNotesFormatter.ToPlainText(
            "## Highlights\n"
            + "\n"
            + "**Uploads use every connection a host allows, at the speed you actually asked for.** Five hosters now\n"
            + "send a file's parts in parallel instead of one after another, and the speed limit — which was quietly\n"
            + "multiplying itself across concurrent uploads — became a single shared budget.\n"
            + "\n"
            + "- **Progress is aggregated, not per part.** The bar is one monotonic total for the file, which is\n"
            + "  harder than it sounds when parts complete out of order and report their own offsets.\n"
            + "- **The first failure wins by part number, not by timing.** If parts 2 and 5 both fail, you are told\n"
            + "  about part 2 — the earliest problem, not whichever thread reached the error handler first.\n")!;

        string[] lines = text.Split('\n');
        Assert.Equal("Highlights", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.StartsWith("Uploads use every connection", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("a single shared budget.", lines[2], StringComparison.Ordinal); // one joined paragraph
        Assert.Equal(string.Empty, lines[3]);
        Assert.StartsWith("• Progress is aggregated", lines[4], StringComparison.Ordinal);
        Assert.StartsWith("• The first failure wins", lines[5], StringComparison.Ordinal);
        Assert.Equal(6, lines.Length);
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
    }

    /// <summary>GitHub's generated notes — the fallback input when no hand-written file exists.</summary>
    [Fact]
    public void GitHubGeneratedNotes_RenderReadably()
    {
        string text = ReleaseNotesFormatter.ToPlainText(
            "## What's Changed\n"
            + "* fix(wizard): stop promising drag-and-drop on Linux by @NeWbY100 in https://github.com/CSUploader/CSUploader/pull/12\n"
            + "* feat(settings): one default upload directory by @NeWbY100 in https://github.com/CSUploader/CSUploader/pull/13\n"
            + "\n"
            + "**Full Changelog**: https://github.com/CSUploader/CSUploader/compare/v1.4.4...v1.4.5\n")!;

        string[] lines = text.Split('\n');
        Assert.Equal("What's Changed", lines[0]);
        Assert.StartsWith("• fix(wizard):", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("• feat(settings):", lines[3], StringComparison.Ordinal);
        Assert.Contains("Full Changelog: https://github.com/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
    }
}
