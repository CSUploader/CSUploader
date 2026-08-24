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
                Result = new StartupUpdatePromptResult(false, AskAtStartupCheck.IsChecked == true);
            }
        };

        // Later, not "Update now": the safe option is the one a stray Return should not trigger. The
        // accent button remains IsDefault so Return still means yes for a user who read the dialog.
        Opened += (_, _) => LaterButton.Focus();
    }

    /// <summary>The answer. Set exactly once, whichever route the window closes by.</summary>
    public StartupUpdatePromptResult Result { get; private set; }

    public void SetVersions(string newVersion, string currentVersion, bool askAtStartup)
    {
        MessageText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["UpdatePrompt_Message_Format"],
            newVersion,
            currentVersion);
        AskAtStartupCheck.IsChecked = askAtStartup;
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
        Result = new StartupUpdatePromptResult(updateNow, AskAtStartupCheck.IsChecked == true);
        UpdateNowButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Close();
    }
}
