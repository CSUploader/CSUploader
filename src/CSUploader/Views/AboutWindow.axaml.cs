// <copyright file="AboutWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CSUploader.Lib;
using CSUploader.Lib.Localization;

namespace CSUploader.Views;

/// <summary>
/// About box (port of the WPF <c>AboutWindow</c>). No IDialogService member — the production opener is
/// the MainWindow menu (Phase 6/7); the gallery button is its only opener until then.
/// </summary>
/// <remarks>
/// The version line comes from <see cref="AppVersion"/>, the same value the update prompt shows as
/// "you have" and the same one a loose build compares against the release feed. (An INSTALLED build's
/// comparison is Velopack's own, against its locator's package version, not this.) It used to read <c>GetExecutingAssembly().GetName().Version</c>, which is pinned to a
/// literal in the head's csproj and therefore does NOT follow release.yml's <c>-p:Version=</c> — so a
/// shipped 1.6.0 would have gone on introducing itself as 1.5.0.0 here while the update prompt next
/// to it said 1.6.0.
/// </remarks>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["About_Version_Format"], AppVersion.Current);
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
