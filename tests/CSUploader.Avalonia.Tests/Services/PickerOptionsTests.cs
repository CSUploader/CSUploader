// <copyright file="PickerOptionsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Platform.Storage;
using CSUploader.Services;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// Pins the <see cref="AvaloniaDialogService"/> StorageProvider option builders (Phase 4 Task 4).
/// The pickers themselves are native and cannot run headlessly (Reality-check #11), so the mapping
/// wrinkles are asserted on the pure <c>internal static</c> builders instead: the Win32-filter →
/// <see cref="FilePickerFileType"/> mapping via the Core <c>FileDialogFilterParser</c>, the save
/// picker's <c>DefaultExtension</c> TrimStart, the multi-select flag, suggested-name pass-through,
/// and the null-lenience that degrades a missing filter to "no filter". Plain <c>[Fact]</c>: the
/// option types are framework POCOs needing no Avalonia app context.
/// </summary>
public class PickerOptionsTests
{
    // ── BuildSaveOptions: DefaultExtension TrimStart wrinkle ──────────────────────────────────────

    [Theory]
    [InlineData(".txt", "txt")]     // dotted (what every WPF caller passes) → bare, Avalonia's form
    [InlineData(".json", "json")]
    [InlineData("json", "json")]    // already bare → unchanged
    [InlineData("..tar", "tar")]    // TrimStart removes every leading dot
    public void BuildSaveOptions_DefaultExtension_TrimsLeadingDots(string defaultExt, string expected)
    {
        FilePickerSaveOptions options = AvaloniaDialogService.BuildSaveOptions(null, null, defaultExt);

        Assert.Equal(expected, options.DefaultExtension);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildSaveOptions_NullOrEmptyDefaultExtension_YieldsNull(string? defaultExt)
    {
        FilePickerSaveOptions options = AvaloniaDialogService.BuildSaveOptions(null, null, defaultExt);

        Assert.Null(options.DefaultExtension);
    }

    [Fact]
    public void BuildSaveOptions_SuggestedName_PassesThrough()
    {
        FilePickerSaveOptions options = AvaloniaDialogService.BuildSaveOptions("export.json", null, null);

        Assert.Equal("export.json", options.SuggestedFileName);
    }

    [Fact]
    public void BuildSaveOptions_NullSuggestedName_IsNull()
    {
        FilePickerSaveOptions options = AvaloniaDialogService.BuildSaveOptions(null, null, null);

        Assert.Null(options.SuggestedFileName);
    }

    [Fact]
    public void BuildSaveOptions_ShowOverwritePrompt_LeftAtPlatformDefault()
    {
        // Reality-check finding (the plan assumed the default was `true`): FilePickerSaveOptions
        // .ShowOverwritePrompt is a bool? whose default is null — "use the platform default", which on
        // Windows is prompt-on-overwrite, so leaving it unset preserves WPF SaveFileDialog's warn-before-
        // clobber behavior without hardcoding a value.
        FilePickerSaveOptions options = AvaloniaDialogService.BuildSaveOptions("f.json", null, null);

        Assert.Null(options.ShowOverwritePrompt);
    }

    [Fact]
    public void BuildSaveOptions_Filter_MapsToFileTypeChoices()
    {
        FilePickerSaveOptions options = AvaloniaDialogService.BuildSaveOptions(
            "export.json", "JSON files (*.json)|*.json|All files (*.*)|*.*", ".json");

        Assert.NotNull(options.FileTypeChoices);
        Assert.Equal(2, options.FileTypeChoices!.Count);
        Assert.Equal("JSON files (*.json)", options.FileTypeChoices[0].Name);
        Assert.Equal(["*.json"], options.FileTypeChoices[0].Patterns!);
        Assert.Equal("All files (*.*)", options.FileTypeChoices[1].Name);
        Assert.Equal(["*.*"], options.FileTypeChoices[1].Patterns!);
    }

    [Fact]
    public void BuildSaveOptions_NullFilter_YieldsNullFileTypeChoices()
    {
        FilePickerSaveOptions options = AvaloniaDialogService.BuildSaveOptions("f", null, null);

        Assert.Null(options.FileTypeChoices);
    }

    // ── BuildOpenOptions: multi-select flag + filter mapping ──────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildOpenOptions_Multiple_SetsAllowMultiple(bool multiple)
    {
        FilePickerOpenOptions options = AvaloniaDialogService.BuildOpenOptions("Pick", null, multiple);

        Assert.Equal(multiple, options.AllowMultiple);
    }

    [Fact]
    public void BuildOpenOptions_Title_PassesThrough()
    {
        FilePickerOpenOptions options = AvaloniaDialogService.BuildOpenOptions("Choose files", null, multiple: true);

        Assert.Equal("Choose files", options.Title);
    }

    [Fact]
    public void BuildOpenOptions_MultiPatternFilter_MapsPatternsAndNames()
    {
        // Semicolon-split patterns and no-parenthetical names, exercising the parser's split rules
        // end to end through the builder.
        FilePickerOpenOptions options = AvaloniaDialogService.BuildOpenOptions(
            "Pick", "All files|*.*|Text|*.txt;*.log", multiple: true);

        Assert.NotNull(options.FileTypeFilter);
        Assert.Equal(2, options.FileTypeFilter!.Count);
        Assert.Equal("All files", options.FileTypeFilter[0].Name);
        Assert.Equal(["*.*"], options.FileTypeFilter[0].Patterns!);
        Assert.Equal("Text", options.FileTypeFilter[1].Name);
        Assert.Equal(["*.txt", "*.log"], options.FileTypeFilter[1].Patterns!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildOpenOptions_NullOrBlankFilter_YieldsNullFileTypeFilter(string? filter)
    {
        FilePickerOpenOptions options = AvaloniaDialogService.BuildOpenOptions("Pick", filter, multiple: false);

        Assert.Null(options.FileTypeFilter);
    }

    // ── BuildFolderOptions: title, single-select, suggested start location ────────────────────────

    [Fact]
    public void BuildFolderOptions_Title_PassesThrough_And_IsSingleSelect()
    {
        FolderPickerOpenOptions options = AvaloniaDialogService.BuildFolderOptions("Select a folder", suggestedStartLocation: null);

        Assert.Equal("Select a folder", options.Title);
        Assert.False(options.AllowMultiple);
    }

    [Fact]
    public void BuildFolderOptions_NullStartLocation_IsNull()
    {
        // The member passes null when initialDirectory is absent or TryGetFolderFromPathAsync couldn't
        // resolve it (Reality-check #10) — the builder must carry that through as "no suggestion".
        FolderPickerOpenOptions options = AvaloniaDialogService.BuildFolderOptions("Select", suggestedStartLocation: null);

        Assert.Null(options.SuggestedStartLocation);
    }
}
