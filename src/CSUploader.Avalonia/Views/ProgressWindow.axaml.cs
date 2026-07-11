// <copyright file="ProgressWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;

namespace CSUploader.Views;

/// <summary>
/// Modal progress window (port of the WPF <c>ProgressWindow</c>), MINIMAL: the window shell plus the
/// <c>LabelText</c>/<c>CancelButton</c> surface, WITHOUT the WPF <c>ExecuteAsync</c> driver.
/// </summary>
/// <remarks>
/// No current callers on either head — the WPF <c>ProgressWindow.ExecuteAsync</c> has been unused since the
/// WinForms→WPF migration (orphaned since commit 6d68070). Ported for parity as the reusable modal-progress
/// primitive; the keep-vs-delete decision is deferred to the Phase 4 gate (plan §Reality-check #13, surfaced
/// at Task 9). With no <c>ExecuteAsync</c> there is no <see cref="System.Threading.CancellationTokenSource"/>
/// to signal, so <see cref="CancelButton_Click"/> only reproduces the WPF visible behavior (relabel +
/// disable); a real consumer would re-add the async driver and the cancellation wiring.
/// </remarks>
public partial class ProgressWindow : Window
{
    public ProgressWindow()
    {
        InitializeComponent();
    }

    // Relabel + disable only (WPF parity minus the CancellationTokenSource, which lived in ExecuteAsync).
    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelButton.Content = Localizer.Instance["Progress_BtnCancelling"];
    }
}
