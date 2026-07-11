// <copyright file="SpeedLimitDialog.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;
using CSUploader.Services;

namespace CSUploader.Views;

/// <summary>
/// Prompts for a per-selection upload speed limit in KB/s (port of the WPF <c>SpeedLimitDialog</c>).
/// <c>ShowDialog&lt;SpeedLimitSelection?&gt;</c> encodes the two-level nullability: Cancel/Esc/window-X →
/// <c>null</c> (leave limits untouched); OK-valid → a selection carrying the limit; OK-empty and Clear →
/// a selection whose <see cref="SpeedLimitSelection.LimitKBps"/> is null (revert to the global/inherited
/// value). Invalid input warns and keeps the dialog open (WPF parity).
/// </summary>
public partial class SpeedLimitDialog : Window
{
    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses
    // the currentLimit overload.
    public SpeedLimitDialog()
        : this(null)
    {
    }

    public SpeedLimitDialog(int? currentLimit)
    {
        InitializeComponent();
        LimitBox.Text = currentLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        // Focus + select-all move to Opened: the controls aren't attached to the visual tree at ctor time
        // in Avalonia (port rule 16), so focusing LimitBox in the ctor would no-op.
        Opened += (_, _) =>
        {
            LimitBox.Focus();
            LimitBox.SelectAll();
        };
    }

    // Clear → the "cleared" selection (limit null): revert to the global/inherited value. This is the WPF
    // DialogResult=true, Result=null outcome, now carried as a non-null SpeedLimitSelection(null). The
    // boxed struct (vs Close(null)) is what distinguishes "cleared" from "cancelled" at ShowDialog<T>.
    private void ClearButton_Click(object? sender, RoutedEventArgs e) => Close(new SpeedLimitSelection(null));

    // Cancel/Esc → null (leave limits untouched). WPF's Cancel button had no handler (IsCancel auto-closed
    // with DialogResult=false); Avalonia's IsCancel only routes Esc to Click without closing (port rule 7),
    // so close explicitly here.
    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        string text = (LimitBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            // Empty → the same "cleared" outcome as Clear (WPF parity).
            Close(new SpeedLimitSelection(null));
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) || limit <= 0)
        {
            // Non-positive / non-numeric: warn and stay open so the user can correct it. WPF used a blocking
            // MessageBox.Show; the Avalonia custom box shows modally over this dialog (owner = this).
            await MessageBoxWindow.ShowErrorAsync(
                this,
                Localizer.Instance["SpeedLimit_Validation_Message"],
                Localizer.Instance["SpeedLimit_Validation_Title"]);
            return;
        }

        Close(new SpeedLimitSelection(limit));
    }
}
