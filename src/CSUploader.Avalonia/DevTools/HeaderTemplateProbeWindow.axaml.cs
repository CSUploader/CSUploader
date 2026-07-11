// <copyright file="HeaderTemplateProbeWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CSUploader.DevTools;

/// <summary>
/// DEBUG-only THROWAWAY header-template probe (Phase 6 Task 1, prep item 1): a plain window holding one
/// <see cref="DataGrid"/> whose column headers are re-templated via a custom
/// <c>DataGridColumnHeader</c> <see cref="Avalonia.Styling.ControlTheme"/> (wired through
/// <see cref="DataGrid.ColumnHeaderTheme"/>). It pins — go/no-go — whether UploadsView (Task 10) can
/// carry the JD2 lock <see cref="ToggleButton"/> + drag-resize + sort in a custom header before the real
/// view invests in the recipe. Task 10 copies the recipe (or takes the stock-header fallback) and
/// DELETES this window; the probe tests fold into <c>UploadsViewTests</c> at that point.
/// </summary>
/// <remarks>
/// Findings pinned by ILSpy over the installed 11.3.13 <c>Avalonia.Controls.DataGridColumnHeader</c>:
/// the header has NO <c>OnApplyTemplate</c> / <c>NameScope</c> lookups (re-template is free-form; only a
/// <c>ContentPresenter</c> for the header <c>Content</c> is required), there is NO
/// <c>PART_LeftHeaderGripper</c>/<c>PART_RightHeaderGripper</c> (resize is the header's own 5px-edge
/// pointer logic, gated on <see cref="DataGridColumn.CanUserResize"/>), and sort is the header's
/// pointer-released handler — both survive any hit-testable template. <c>OwningColumn</c>/<c>HeaderCell</c>
/// are <c>internal</c>, so the lock handler associates a header to its column by matching the public
/// <c>Content</c> to a column's public <see cref="DataGridColumn.Header"/> (headers are unique).
/// </remarks>
public partial class HeaderTemplateProbeWindow : Window
{
    public HeaderTemplateProbeWindow()
    {
        InitializeComponent();
        ProbeGrid.ItemsSource = Rows;
    }

    /// <summary>A probe row (public props so the reflection-bound text columns resolve).</summary>
    internal sealed record ProbeRow(string Name, string Size, string Status, int Order);

    /// <summary>
    /// Six rows in a deliberately NON-alphabetical Name order, so a sort click visibly reorders them
    /// (the sort-survival check). Exposed <c>internal</c> so <c>HeaderTemplateProbeTests</c> binds the
    /// same fixture. Names are neutral <c>fake_*</c> placeholders (no hoster URLs — Defender-ML).
    /// </summary>
    internal static readonly ProbeRow[] Rows =
    [
        new("fake_movie.mkv", "5.0 MB", "Paused", 3),
        new("fake_notes.txt", "1.0 MB", "Queued", 1),
        new("fake_archive.zip", "3.0 MB", "Failed", 5),
        new("fake_song.mp3", "2.0 MB", "Completed", 2),
        new("fake_photo.jpg", "1.0 MB", "Completed", 6),
        new("fake_report.pdf", "1.0 MB", "Queued", 4),
    ];

    /// <summary>
    /// Toggles the owning column's width lock (mirrors <c>UploadsView.xaml.cs</c> <c>ColumnLock_Click</c>).
    /// Setting <see cref="DataGridColumn.CanUserResize"/> to false makes the header's built-in drag-resize
    /// a no-op for that column (ILSpy: <c>CanResizeColumn</c> reads <c>ActualCanUserResize</c>).
    /// </summary>
    private void ColumnLock_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            ApplyColumnLock(toggle);
        }
    }

    /// <summary>
    /// The lock body, split from the click handler so a headless test can drive it after setting
    /// <see cref="ToggleButton.IsChecked"/> (raising a real pointer click on a templated toggle is flaky
    /// headlessly — the Phase 5 "extract into an internal helper" pattern).
    /// </summary>
    internal void ApplyColumnLock(ToggleButton toggle)
    {
        if (toggle.FindAncestorOfType<DataGridColumnHeader>() is not { } header)
        {
            return;
        }

        // OwningColumn / HeaderCell are internal on 11.3.13 — associate the header to its column via the
        // public Content (== the column's Header). Reflection on OwningColumn is the content-independent
        // alternative; content-match is public-API and generalizes to UploadsView's unique headers.
        DataGridColumn? column = ProbeGrid.Columns.FirstOrDefault(c => Equals(c.Header, header.Content));
        if (column is not null)
        {
            column.CanUserResize = toggle.IsChecked != true;
        }
    }
}
