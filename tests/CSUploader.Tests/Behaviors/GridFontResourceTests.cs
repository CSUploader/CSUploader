// <copyright file="GridFontResourceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CSUploader.Tests.Avalonia.Behaviors;

// Does Settings > General > Grid Appearance (GridFontFamily / GridFontSize resources, updated live by
// AvaloniaThemeApplier.ApplyGridFont) reach the cell TEXT of a DataGrid that relies only on the global
// BaseStyles "DataGrid" style (as the Uploads and History grids do)? Realize a bare grid, flip the
// resources, and read a realized cell's TextBlock.
public class GridFontResourceTests
{
    private sealed record RowItem(string Name);

    [AvaloniaFact]
    public void GridFontResources_FlowToCellText()
    {
        object? origSize = Application.Current!.Resources.TryGetValue("GridFontSize", out object? s) ? s : null;
        object? origFamily = Application.Current!.Resources.TryGetValue("GridFontFamily", out object? f) ? f : null;
        try
        {
            Application.Current!.Resources["GridFontSize"] = 21.0;
            Application.Current!.Resources["GridFontFamily"] = new FontFamily("Courier New");

            var items = new ObservableCollection<RowItem> { new("hello") };
            var grid = new DataGrid
            {
                ItemsSource = items,
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Width = 300,
                Height = 120,
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding("Name") });

            var window = new Window { Width = 320, Height = 140, Content = grid };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TextBlock? cell = grid.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "hello");
            Assert.True(cell is not null, "the cell TextBlock did not realize headlessly");

            Assert.Equal(21.0, cell!.FontSize);
            Assert.Equal("Courier New", cell.FontFamily.Name);

            window.Close();
        }
        finally
        {
            Application.Current!.Resources["GridFontSize"] = origSize;
            Application.Current!.Resources["GridFontFamily"] = origFamily;
        }
    }
}
