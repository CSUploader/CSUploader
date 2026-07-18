// <copyright file="DataGridSelectionBrushTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls; // ResourceNodeExtensions.TryFindResource
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace CSUploader.Tests.Avalonia.Resources;

// The Avalonia DataGrid Fluent theme paints the selected row with a translucent SystemAccent
// (DataGridRowSelected*BackgroundBrush), which drowns the dark row text. ThemeBrushes.axaml overrides
// those four keys with the flat WPF SelectionBrush colour. TryFindResource walks the full app chain
// (App.Resources over the theme's App.Styles resources), so a match here proves the override both
// exists under the right keys AND wins over the built-in theme — for each ThemeVariant.
public class DataGridSelectionBrushTests
{
    private static readonly string[] SelectionKeys =
    [
        "DataGridRowSelectedBackgroundBrush",
        "DataGridRowSelectedHoveredBackgroundBrush",
        "DataGridRowSelectedUnfocusedBackgroundBrush",
        "DataGridRowSelectedHoveredUnfocusedBackgroundBrush",
    ];

    [AvaloniaFact]
    public void Light_SelectionBrushes_AreTheWpfLightBlue()
    {
        foreach (string key in SelectionKeys)
        {
            Assert.Equal(Color.Parse("#B8D4EE"), ResolvedColor(key, ThemeVariant.Light));
        }
    }

    [AvaloniaFact]
    public void Dark_SelectionBrushes_AreTheWpfDarkBlue()
    {
        foreach (string key in SelectionKeys)
        {
            Assert.Equal(Color.Parse("#334878"), ResolvedColor(key, ThemeVariant.Dark));
        }
    }

    private static Color ResolvedColor(string key, ThemeVariant variant)
    {
        Assert.True(
            Application.Current!.TryFindResource(key, variant, out object? value),
            $"{key} did not resolve for {variant}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
