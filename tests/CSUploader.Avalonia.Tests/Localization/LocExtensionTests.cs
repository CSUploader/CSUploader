// <copyright file="LocExtensionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CSUploader.Lib.Localization;

namespace CSUploader.Tests.Avalonia.Localization;

/// <summary>
/// Prep-item-8 verification of the Avalonia <see cref="LocExtension"/>: a <c>{loc:Loc key}</c>
/// binding must resolve to the active-culture value AND live-update when a culture switch raises
/// <see cref="Localizer"/>'s <c>PropertyChanged("Item[]")</c> — the invalidation chain 20 later
/// view ports depend on. Covered against BOTH target-property kinds: a styled property
/// (<see cref="TextBlock.Text"/>) and a DirectProperty (<see cref="DataGridColumn.Header"/>, the
/// grid-header case, Reality-check register #7). <see cref="Localizer.Instance"/> is process-global,
/// so every culture-touching test restores the original culture in a <c>finally</c>.
/// </summary>
public class LocExtensionTests
{
    [AvaloniaFact]
    public void CultureSwitch_ReEvaluatesLocBinding_OnStyledProperty()
    {
        CultureInfo original = Localizer.Instance.Culture;
        try
        {
            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("en");
            var textBlock = (TextBlock)AvaloniaRuntimeXamlLoader.Load(
                """
                <TextBlock xmlns="https://github.com/avaloniaui"
                           xmlns:loc="clr-namespace:CSUploader.Lib.Localization;assembly=CSUploader.Avalonia"
                           Text="{loc:Loc Main_Tab_Uploads}" />
                """);
            var window = new Window { Content = textBlock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Uploads", textBlock.Text);              // en neutral value (i18n-inventory.md:82)

            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("ja");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("アップロード", textBlock.Text);           // ja satellite (i18n-inventory.ja.md:81)
            window.Close();
        }
        finally
        {
            Localizer.Instance.Culture = original;
        }
    }

    [AvaloniaFact]
    public void CultureSwitch_ReEvaluatesLocBinding_OnDirectProperty()
    {
        // The grid-header case: DataGridColumn.Header is a DirectProperty<DataGridColumn, object>
        // (bindable, OneWay default). This test answers the one open question from Reality-check #7 —
        // does the explicit-Source loc binding not only ATTACH but also live-update on Item[]
        // invalidation? Assert both the initial localized header and the post-switch retitle.
        CultureInfo original = Localizer.Instance.Culture;
        try
        {
            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("en");
            var dataGrid = (DataGrid)AvaloniaRuntimeXamlLoader.Load(
                """
                <DataGrid xmlns="https://github.com/avaloniaui"
                          xmlns:loc="clr-namespace:CSUploader.Lib.Localization;assembly=CSUploader.Avalonia">
                  <DataGrid.Columns>
                    <DataGridTextColumn Header="{loc:Loc Main_Tab_Uploads}" />
                  </DataGrid.Columns>
                </DataGrid>
                """);
            var window = new Window { Content = dataGrid };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGridColumn column = dataGrid.Columns[0];
            Assert.Equal("Uploads", column.Header);               // en neutral value

            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("ja");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("アップロード", column.Header);            // live-updated on Item[] invalidation
            window.Close();
        }
        finally
        {
            Localizer.Instance.Culture = original;
        }
    }
}
