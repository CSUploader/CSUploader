// <copyright file="GroupingProbeWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Collections;
using Avalonia.Controls;

namespace CSUploader.DevTools;

/// <summary>
/// DEBUG-only THROWAWAY grouping probe (Phase 5 Task 2, prep item 6): a plain window holding one
/// grouped <see cref="DataGrid"/> so the DataGridCollectionView + DataGridPathGroupDescription recipe
/// and the re-templated <c>DataGridRowGroupHeader</c> can be pinned (go/no-go) BEFORE UploadedView
/// (Task 5) invests in them. Task 5 copies the recipe into the real view and DELETES this window; the
/// grouping tests retarget to UploadedViewTests at that point.
/// </summary>
/// <remarks>
/// The seven-row fixture mirrors the Task 1 seed's shape in miniature — three package groups of uneven
/// size, one null URL — so the probe exercises exactly what UploadedView will: multiple groups, an empty
/// URL cell, and (via <c>ClipboardCopyMode="IncludeHeader"</c>) the built-in Ctrl+C path on a grouped view.
/// The URL values are neutral placeholders (a Defender ML false-positive quarantined the source when the
/// realistic file-hoster URLs from the plan's Step-1 fixture were present); only the null-URL row is
/// load-bearing for the recipe, and no test asserts a URL value.
/// </remarks>
public partial class GroupingProbeWindow : Window
{
    public GroupingProbeWindow()
    {
        InitializeComponent();

        // The grouped ItemsSource is built in the HEAD (code-behind), not Core: Avalonia.Collections
        // cannot live in the framework-free Core VMs (Phase 1's ICollectionView purge contract). This is
        // the exact shape Task 5's UploadedView.DataContextChanged will use over the VM's raw collection.
        ProbeGrid.ItemsSource = BuildView();
    }

    /// <summary>A file row in the probe fixture (public props so the reflection-bound columns resolve).</summary>
    internal sealed record ProbeRow(string PackageName, string FileName, string Size, string? Url);

    /// <summary>
    /// Three groups, uneven sizes, one null Url — the UploadedView shape in miniature. Exposed
    /// <c>internal</c> so <c>GroupingProbeTests</c> can build the same view and assert grouping/collapse.
    /// </summary>
    internal static readonly ProbeRow[] Rows =
    [
        new("Fake pack (photos)", "fake_beach.jpg", "1.0 MB", "https://example.test/dl/fake02.jpg"),
        new("Fake pack (photos)", "fake_sunset.png", "2.0 MB", "https://example.test/dl/fake03.png"),
        new("Fake pack (photos)", "fake_pano.raw", "2.0 MB", null),
        new("Fake pack (documents)", "fake_report.pdf", "1.0 MB", "https://example.test/dl/fake04.pdf"),
        new("Fake pack (documents)", "fake_specs.docx", "1.0 MB", "https://example.test/dl/fake05.docx"),
        new("Fake pack (archive set)", "fake_part1.rar", "3.0 MB", "https://example.test/dl/fake06.rar"),
        new("Fake pack (archive set)", "fake_part2.rar", "3.0 MB", "https://example.test/dl/fake07.rar"),
    ];

    /// <summary>Builds the grouped view Task 5 copies: one path-group description over PackageName.</summary>
    internal static DataGridCollectionView BuildView()
    {
        DataGridCollectionView view = new(Rows);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(ProbeRow.PackageName)));
        return view;
    }
}
