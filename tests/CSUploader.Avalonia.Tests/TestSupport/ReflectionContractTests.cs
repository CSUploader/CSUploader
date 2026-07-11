// <copyright file="ReflectionContractTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Canary gate for the private implementation details the leak/teardown tests reach into by reflection,
/// and for the text-shape assumptions the XAML drift gates bake into their regex parses. These are all
/// silent-failure surfaces: a framework rename of a private backing field would make a reflected lookup
/// return null and the dependent test would then measure 0 subscribers and PASS vacuously; a
/// commented-out <c>x:Key</c> or a second Dark-variant marker would skew a drift parse without any
/// direct symptom. Failing here — loudly, naming the dependent test — beats a green suite that no longer
/// checks anything.
/// </summary>
public class ReflectionContractTests
{
    [Fact]
    public void Interactive_EventHandlersField_StillExists()
    {
        // DataGridBehaviorTests.PointerPressedHandlerCount reads this to count a behavior's handler delta.
        FieldInfo? field = typeof(Interactive).GetField("_eventHandlers", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(
            field is not null,
            "Avalonia.Interactivity.Interactive._eventHandlers was renamed/removed — "
            + "DataGridBehaviorTests.PointerPressedHandlerCount would silently count 0 handlers and pass vacuously.");
    }

    [Fact]
    public void WindowBase_IsActiveBackingField_StillExists()
    {
        // DialogOwnerResolverTests.SetIsActive force-sets this private field to construct the active-but-
        // hidden and visible-but-inactive windows headless input can't produce (HiddenActiveIsSkipped,
        // InactiveVisibleWindow_IsSkipped_VisibleMainWins). Those tests self-guard with a throw on a null
        // lookup, but registering it here keeps the canary registry complete and names the dependents.
        FieldInfo? field = typeof(WindowBase).GetField("_isActive", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(
            field is not null,
            "Avalonia.Controls.WindowBase._isActive was renamed/removed — DialogOwnerResolverTests.SetIsActive "
            + "(HiddenActiveIsSkipped, InactiveVisibleWindow_IsSkipped_VisibleMainWins) would throw on the lookup.");
    }

    [Fact]
    public void Localizer_PropertyChangedBackingField_StillExists()
    {
        // LocExtensionTests.SubscriberCount reads this to count live weak subscribers on the singleton.
        FieldInfo? field = typeof(Localizer).GetField(
            nameof(Localizer.PropertyChanged), BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(
            field is not null,
            "Localizer's field-like PropertyChanged backing field is gone (event turned into an explicit "
            + "add/remove?) — LocExtensionTests.SubscriberCount would silently measure 0 and pass vacuously.");
    }

    [Fact]
    public void ObservableCollection_CollectionChangedBackingField_StillExists()
    {
        // DataGridBehaviorTests.CollectionChangedSubscriberCount reads this off the bound source collection.
        FieldInfo? field = typeof(ObservableCollection<int>).GetField(
            "CollectionChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(
            field is not null,
            "ObservableCollection<T>'s field-like CollectionChanged backing field is gone — "
            + "DataGridBehaviorTests.CollectionChangedSubscriberCount would silently measure 0 and pass vacuously.");
    }

    [Fact]
    public void ThemeBrushes_HasExactlyOneDarkVariantMarker()
    {
        // ThemeTests.AvaloniaVariantKeys splits ThemeBrushes.axaml on the single "ThemeVariant.Dark"
        // marker to isolate each variant's key set. A second occurrence would split mid-Dark-section and
        // silently under-count the Dark keys, weakening the set-equality drift gate.
        string themeBrushes = File.ReadAllText(Path.Combine(
            RepoXaml.FindRepoRoot(), "src", "CSUploader.Avalonia", "Resources", "ThemeBrushes.axaml"));
        int occurrences = Regex.Matches(themeBrushes, "ThemeVariant\\.Dark").Count;
        Assert.True(
            occurrences == 1,
            $"ThemeBrushes.axaml has {occurrences} 'ThemeVariant.Dark' occurrences, expected exactly 1 — "
            + "ThemeTests.AvaloniaVariantKeys assumes one Dark dictionary marker for its variant split.");
    }

    [Fact]
    public void NoDriftGateFile_HidesAnXKeyInsideAnXmlComment()
    {
        // The drift gates parse x:Key with a flat regex that does not skip XML comments, so a commented-out
        // <!-- ... x:Key="Foo" ... --> would be counted as a live key and mask a real drift. None of the
        // five parsed files may contain an x:Key inside a comment.
        string root = RepoXaml.FindRepoRoot();
        string[] parsedFiles =
        [
            Path.Combine(root, "src", "Resources", "ImageResources.xaml"),
            Path.Combine(root, "src", "Resources", "Theme.Light.xaml"),
            Path.Combine(root, "src", "Resources", "Theme.Dark.xaml"),
            Path.Combine(root, "src", "CSUploader.Avalonia", "Resources", "ImageGeometries.axaml"),
            Path.Combine(root, "src", "CSUploader.Avalonia", "Resources", "ThemeBrushes.axaml"),
        ];

        foreach (string file in parsedFiles)
        {
            string text = File.ReadAllText(file);
            foreach (Match comment in Regex.Matches(text, "<!--.*?-->", RegexOptions.Singleline))
            {
                Assert.False(
                    comment.Value.Contains("x:Key", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} hides an x:Key inside an XML comment — a drift-gate regex parse "
                    + "would count it as a live key. Remove the commented-out key or the whole comment.");
            }
        }
    }
}
