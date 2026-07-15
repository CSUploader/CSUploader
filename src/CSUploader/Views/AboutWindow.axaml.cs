// <copyright file="AboutWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;

namespace CSUploader.Views;

/// <summary>
/// About box (port of the WPF <c>AboutWindow</c>). No IDialogService member — the production opener is
/// the MainWindow menu (Phase 6/7); the gallery button is its only opener until then.
/// </summary>
/// <remarks>
/// The version line resolves <see cref="Assembly.GetExecutingAssembly"/>'s version, which is the
/// assembly default <c>1.0.0</c> on the Avalonia head (its csproj declares no Version, unlike the WPF
/// csproj). Accepted, noted Phase 4 divergence: About has no production opener until Phase 6/7, and real
/// version alignment belongs to the Phase 9 Velopack cutover.
/// </remarks>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        VersionText.Text = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["About_Version_Format"], version);
    }

    // OK closes the window. The WPF button was IsCancel+IsDefault with NO handler (WPF's IsCancel
    // auto-closed it); Avalonia's IsCancel/IsDefault only route Esc/Enter to Click without closing
    // (port rule 7), so the port MUST add this explicit close — the documented AboutWindow gotcha.
    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Opens the URL on the clicked TextBlock's Tag in the default browser. A plain TextBlock + pointer
    /// handler (not a Hyperlink) mirrors the WPF port, which sidestepped WPF's hyperlink pipeline for a
    /// Win11 "Run is not a Visual or Visual3D" bug; the Avalonia idiom is <c>PointerReleased</c> with a
    /// left-button guard (port rule 10).
    /// </summary>
    private void GithubLink_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left ||
            sender is not TextBlock tb || tb.Tag is not string url || string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; failing silently is fine for a link click.
        }

        e.Handled = true;
    }
}
