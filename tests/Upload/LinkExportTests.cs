// <copyright file="LinkExportTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using Xunit;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Pins <see cref="LinkExport"/>'s exact output — the "Copy Links" text is pasted verbatim into posts,
/// so the shapes (headers, link lines, blank line between blocks, no trailing newline, first-seen group
/// order) are the contract.
/// </summary>
public class LinkExportTests
{
    private static readonly LinkExportRow[] Rows =
    [
        new("a.r00", "Rapidgator", "https://rg/a0"),
        new("a.r00", "KatFile", "https://kf/a0"),
        new("a.r01", "Rapidgator", "https://rg/a1"),
    ];

    private static string L(params string[] lines) => string.Join(Environment.NewLine, lines);

    [Fact]
    public void Format_ByFilePlain_MirrorBlocksPerFile()
        => Assert.Equal(
            L("a.r00", "https://rg/a0", "https://kf/a0", string.Empty, "a.r01", "https://rg/a1"),
            LinkExport.Format(Rows, LinkExportGroupBy.File, LinkExportFormat.Plain));

    [Fact]
    public void Format_ByHosterPlain_GroupsInFirstSeenHosterOrder()
        => Assert.Equal(
            L("Rapidgator", "https://rg/a0", "https://rg/a1", string.Empty, "KatFile", "https://kf/a0"),
            LinkExport.Format(Rows, LinkExportGroupBy.Hoster, LinkExportFormat.Plain));

    [Fact]
    public void Format_ByFileBBCode_BoldHeaderAndUrlTags()
        => Assert.Equal(
            L("[b]a.r00[/b]", "[url]https://rg/a0[/url]", "[url]https://kf/a0[/url]", string.Empty, "[b]a.r01[/b]", "[url]https://rg/a1[/url]"),
            LinkExport.Format(Rows, LinkExportGroupBy.File, LinkExportFormat.BBCode));

    [Fact]
    public void Format_ByHosterBBCode_BoldHosterHeader()
        => Assert.Equal(
            L("[b]Rapidgator[/b]", "[url]https://rg/a0[/url]", "[url]https://rg/a1[/url]", string.Empty, "[b]KatFile[/b]", "[url]https://kf/a0[/url]"),
            LinkExport.Format(Rows, LinkExportGroupBy.Hoster, LinkExportFormat.BBCode));

    [Fact]
    public void Format_ByFileMarkdown_BulletsLabelledWithHoster()
        => Assert.Equal(
            L("**a.r00**", "- [Rapidgator](https://rg/a0)", "- [KatFile](https://kf/a0)", string.Empty, "**a.r01**", "- [Rapidgator](https://rg/a1)"),
            LinkExport.Format(Rows, LinkExportGroupBy.File, LinkExportFormat.Markdown));

    [Fact]
    public void Format_ByHosterMarkdown_BulletsLabelledWithFileName()
        => Assert.Equal(
            L("**Rapidgator**", "- [a.r00](https://rg/a0)", "- [a.r01](https://rg/a1)", string.Empty, "**KatFile**", "- [a.r00](https://kf/a0)"),
            LinkExport.Format(Rows, LinkExportGroupBy.Hoster, LinkExportFormat.Markdown));

    [Fact]
    public void Format_EmptyInput_YieldsEmptyString()
        => Assert.Equal(string.Empty, LinkExport.Format([], LinkExportGroupBy.File, LinkExportFormat.Plain));

    [Theory]
    [InlineData("ByFile.Plain", LinkExportGroupBy.File, LinkExportFormat.Plain)]
    [InlineData("ByFile.BBCode", LinkExportGroupBy.File, LinkExportFormat.BBCode)]
    [InlineData("ByFile.Markdown", LinkExportGroupBy.File, LinkExportFormat.Markdown)]
    [InlineData("ByHoster.Plain", LinkExportGroupBy.Hoster, LinkExportFormat.Plain)]
    [InlineData("ByHoster.BBCode", LinkExportGroupBy.Hoster, LinkExportFormat.BBCode)]
    [InlineData("ByHoster.Markdown", LinkExportGroupBy.Hoster, LinkExportFormat.Markdown)]
    public void ParseKey_MapsEveryMenuKey(string key, LinkExportGroupBy groupBy, LinkExportFormat format)
        => Assert.Equal((groupBy, format), LinkExport.ParseKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ByFile.Html")]
    public void ParseKey_UnknownKey_ReturnsNull(string? key)
        => Assert.Null(LinkExport.ParseKey(key));
}
