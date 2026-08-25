// <copyright file="SplashWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Lib.Localization;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// The startup splash. It has almost no behaviour by design — the startup sequence drives it — so
/// what is worth pinning is that it says something, that screen readers get the same something, and
/// that it is positioned for a window with no owner to be centred on.
/// </summary>
public class SplashWindowTests
{
    [AvaloniaFact]
    public void ItSaysWhatItIsDoing()
    {
        SplashWindow splash = new();
        try
        {
            splash.Show();
            Dispatcher.UIThread.RunJobs();

            // Not just a spinner: an indeterminate bar says SOMETHING is happening, the text says
            // what, and the automation name says it to a screen reader too.
            Assert.Equal(Localizer.Instance["Splash_Status_CheckingForUpdates"], splash.StatusText.Text);
            Assert.Equal(
                splash.StatusText.Text,
                global::Avalonia.Automation.AutomationProperties.GetName(splash.StatusText));
            Assert.True(splash.Progress.IsIndeterminate);
        }
        finally
        {
            splash.Close();
        }
    }

    /// <summary>
    /// It is shown before any other window exists, so there is nothing to centre it ON — only the
    /// screen. CenterOwner with a null owner lands it in a corner.
    /// </summary>
    [AvaloniaFact]
    public void ItCentresOnTheScreen_HavingNoOwnerToCentreOn()
    {
        SplashWindow splash = new();
        try
        {
            Assert.Equal(WindowStartupLocation.CenterScreen, splash.WindowStartupLocation);

            // And it keeps a taskbar entry: it is the application's main window until the real one
            // takes over, and a main window absent from the taskbar cannot be found again.
            Assert.True(splash.ShowInTaskbar);
        }
        finally
        {
            splash.Close();
        }
    }

    [AvaloniaFact]
    public void SetStatus_ChangesTheTextAndTheAutomationName()
    {
        SplashWindow splash = new();
        try
        {
            splash.Show();
            Dispatcher.UIThread.RunJobs();

            splash.SetStatus("Starting…");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Starting…", splash.StatusText.Text);
            Assert.Equal("Starting…", global::Avalonia.Automation.AutomationProperties.GetName(splash.StatusText));
        }
        finally
        {
            splash.Close();
        }
    }
}
