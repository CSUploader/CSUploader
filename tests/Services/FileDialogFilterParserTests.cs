// <copyright file="FileDialogFilterParserTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Services;

namespace CSUploader.Tests.Services;

/// <summary>
/// Pins <see cref="FileDialogFilterParser.Parse"/> against the exact Win32 filter strings the app
/// hands its dialogs today — the JSON export filter (<c>UploadedViewModel.cs</c>) and the proxy-list
/// import filter (<c>Settings_Conn_ImportProxies_FileFilter</c>) — plus the leniency rules the
/// Avalonia StorageProvider pickers rely on so a bad localized filter degrades to "no filter".
/// </summary>
public class FileDialogFilterParserTests
{
    [Fact]
    public void Parse_JsonExportFilter_SplitsIntoTwoNamedGroups()
    {
        IReadOnlyList<FileDialogFilterParser.FilterEntry> entries =
            FileDialogFilterParser.Parse("JSON files (*.json)|*.json|All files (*.*)|*.*");

        Assert.Equal(2, entries.Count);
        Assert.Equal("JSON files (*.json)", entries[0].Name);
        Assert.Equal(["*.json"], entries[0].Patterns);
        Assert.Equal("All files (*.*)", entries[1].Name);
        Assert.Equal(["*.*"], entries[1].Patterns);
    }

    [Fact]
    public void Parse_ProxyImportFilter_SplitsIntoTwoNamedGroups()
    {
        // The literal value of Settings_Conn_ImportProxies_FileFilter (docs/i18n-inventory.md).
        IReadOnlyList<FileDialogFilterParser.FilterEntry> entries =
            FileDialogFilterParser.Parse("Proxy lists (*.txt)|*.txt|All files (*.*)|*.*");

        Assert.Equal(2, entries.Count);
        Assert.Equal("Proxy lists (*.txt)", entries[0].Name);
        Assert.Equal(["*.txt"], entries[0].Patterns);
        Assert.Equal("All files (*.*)", entries[1].Name);
        Assert.Equal(["*.*"], entries[1].Patterns);
    }

    [Fact]
    public void Parse_MultiPatternGroup_SplitsPatternsOnSemicolon()
    {
        IReadOnlyList<FileDialogFilterParser.FilterEntry> entries =
            FileDialogFilterParser.Parse("Images|*.png;*.jpg");

        FileDialogFilterParser.FilterEntry entry = Assert.Single(entries);
        Assert.Equal("Images", entry.Name);
        Assert.Equal(["*.png", "*.jpg"], entry.Patterns);
    }

    [Fact]
    public void Parse_TrailingNameWithoutPatterns_IsDropped()
    {
        // Odd segment count: the final lone name has no patterns and must not emit an entry.
        IReadOnlyList<FileDialogFilterParser.FilterEntry> entries =
            FileDialogFilterParser.Parse("Documents (*.doc)|*.doc|Orphan");

        FileDialogFilterParser.FilterEntry entry = Assert.Single(entries);
        Assert.Equal("Documents (*.doc)", entry.Name);
        Assert.Equal(["*.doc"], entry.Patterns);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundNamesAndPatterns()
    {
        IReadOnlyList<FileDialogFilterParser.FilterEntry> entries =
            FileDialogFilterParser.Parse("  Docs  |  *.doc ; *.docx  ");

        FileDialogFilterParser.FilterEntry entry = Assert.Single(entries);
        Assert.Equal("Docs", entry.Name);
        Assert.Equal(["*.doc", "*.docx"], entry.Patterns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrBlank_YieldsEmptyList(string? filter)
    {
        Assert.Empty(FileDialogFilterParser.Parse(filter));
    }
}
