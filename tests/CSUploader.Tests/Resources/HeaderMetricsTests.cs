// <copyright file="HeaderMetricsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Headless.XUnit;

namespace CSUploader.Tests.Avalonia.Resources;

/// <summary>
/// The one-shot Fluent header-metrics pass (Phase 9 Task 1): the app overrides the DataGrid theme's
/// sort-icon reserve so narrow checkbox-column headers (proxies 'On', accounts '✓') stop clipping to blank.
/// This guards the resource override's presence + value; the visual proof is the Task 1 contact-sheet re-shoot.
/// Runs under <see cref="AvaloniaFactAttribute"/> (not a plain <c>[Fact]</c> as the plan template drafted) because
/// it reads <c>Application.Current</c>'s merged resource surface, which only exists on the headless UI thread —
/// the same reason <see cref="ImageResourceTests"/> uses <c>[AvaloniaFact]</c>.
/// </summary>
public class HeaderMetricsTests
{
    [AvaloniaFact]
    public void App_OverridesDataGridSortIconMinWidth()
    {
        Assert.True(
            Application.Current!.TryGetResource("DataGridSortIconMinWidth", null, out object? value),
            "DataGridSortIconMinWidth override missing — the header-metrics reclaim regressed.");
        Assert.Equal(8d, Assert.IsType<double>(value));
    }
}
