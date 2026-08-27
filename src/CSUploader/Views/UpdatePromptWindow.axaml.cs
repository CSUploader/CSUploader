// <copyright file="UpdatePromptWindow.axaml.cs" company="CSUploader">
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
/// Asks whether to install the update found during startup.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private bool _submitted;

    public UpdatePromptWindow()
    {
        InitializeComponent();
        UpdateNowButton.Click += (_, _) => Submit(updateNow: true);
        LaterButton.Click += (_, _) => Submit(updateNow: false);

        // Every remaining way out - the title bar's X, Alt+F4, the window being closed by the shell -
        // is Later, carrying the checkbox as it stands. Closing without answering is not consent to
        // restart the app, and it is not a reason to discard a preference just changed.
        Closing += (_, _) =>
        {
            if (!_submitted)
            {
                _submitted = true;
                Result = new StartupUpdatePromptResult(false, CheckAtStartupBox.IsChecked == true);
            }
        };

        // Later, not "Update now": the safe option is the one a stray Return should not trigger. The
        // accent button remains IsDefault so Return still means yes for a user who read the dialog.
        Opened += (_, _) => LaterButton.Focus();
    }

    /// <summary>The answer. Set exactly once, whichever route the window closes by.</summary>
    public StartupUpdatePromptResult Result { get; private set; }

    public void SetVersions(string newVersion, string currentVersion, bool checkAtStartup, string? releaseNotesMarkdown = null)
    {
        MessageText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["UpdatePrompt_Message_Format"],
            newVersion,
            currentVersion);
        CheckAtStartupBox.IsChecked = checkAtStartup;

        // Rendered to plain text here, absent entirely when there is nothing to show: packages
        // built before CI embedded notes carry none, and a header over an empty box would read as
        // a bug rather than an absence.
        // Padded on top of the formatting: the formatter emits \n\n between blocks by design, and
        // this is a wrapping TextBlock measured under SizeToContent - see Avalonia12EmptyLineHang.
        string? notes = CSUploader.Lib.UI.Avalonia12EmptyLineHang.PadEmptyLines(
            CSUploader.Lib.Update.ReleaseNotesFormatter.ToPlainText(releaseNotesMarkdown));
        NotesSection.IsVisible = notes is not null;
        NotesText.Text = notes ?? string.Empty;
    }

    private void Submit(bool updateNow)
    {
        // Guarded because "Update now" hands over to an updater that restarts the process: a second
        // click while the first is being acted on must not queue a second install.
        if (_submitted)
        {
            return;
        }

        _submitted = true;
        Result = new StartupUpdatePromptResult(updateNow, CheckAtStartupBox.IsChecked == true);
        UpdateNowButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Close();
    }
}
