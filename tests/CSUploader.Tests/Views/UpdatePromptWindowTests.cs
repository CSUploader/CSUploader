// <copyright file="UpdatePromptWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CSUploader.Views;
using static CSUploader.Tests.Avalonia.HeadlessInput;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// The startup update prompt. Its answer decides whether the app restarts into an installer, so
/// every route out of the window has to produce a deliberate one — including the routes that are
/// not a button.
/// </summary>
public class UpdatePromptWindowTests
{
    private static UpdatePromptWindow Build(bool checkAtStartup = true)
    {
        UpdatePromptWindow window = new();
        window.SetVersions("1.6.0", "1.5.0", checkAtStartup);
        return window;
    }

    /// <summary>
    /// The what's-new section renders the package's embedded notes as plain text, and is absent
    /// ENTIRELY - header included - when the package carries none: every package built before CI
    /// embedded notes has none, and a header over an empty box reads as a bug.
    /// </summary>
    [AvaloniaFact]
    public void WhatsNew_ShowsPlainTextNotes_AndIsAbsentWithoutThem()
    {
        UpdatePromptWindow window = new();
        window.SetVersions("1.6.0", "1.5.0", checkAtStartup: true, releaseNotesMarkdown: "## Highlights\n\n**Bold** thing happened.");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.NotesSection.IsVisible);
            // The blank line carries one space: the Avalonia 12.1.1 empty-line measure hang means
            // this exact window with this exact text WAS the suite's hang until the padding landed.
            Assert.Equal("Highlights" + "\n \n" + "Bold thing happened.", window.NotesText.Text); // rendered, not raw markdown
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Long notes must be reachable from the keyboard: the ScrollViewer is focusable and Page
    /// Down moves it. Both halves regress silently - a non-focusable viewer still LOOKS right.
    /// </summary>
    [AvaloniaFact]
    public void WhatsNew_ScrollsFromTheKeyboard()
    {
        UpdatePromptWindow window = new();
        string longNotes = string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"Paragraph {i} of the notes."));
        window.SetVersions("1.6.0", "1.5.0", checkAtStartup: true, releaseNotesMarkdown: longNotes);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.NotesScroll.Focusable);
            window.NotesScroll.Focus();
            Dispatcher.UIThread.RunJobs();

            double before = window.NotesScroll.Offset.Y;
            Press(window, Key.PageDown, PhysicalKey.PageDown);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.NotesScroll.Offset.Y > before, $"PageDown did not scroll (offset stayed {before}).");
        }
        finally
        {
            window.Close();
        }
    }
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void WhatsNew_AbsentForBlankNotes_HoweverTheyAreBlank(string? notes)
    {
        UpdatePromptWindow window = new();
        window.SetVersions("1.6.0", "1.5.0", checkAtStartup: true, releaseNotesMarkdown: notes);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.NotesSection.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
    [AvaloniaFact]
    public void UpdateNow_ReturnsUpdateNowAndTheCheckboxAsItStands()
    {
        UpdatePromptWindow window = Build();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CheckAtStartupBox.IsChecked = false;
            Click(window.UpdateNowButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.Result.UpdateNow);
            Assert.False(window.Result.CheckAtStartup);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Later_ReturnsNotNow()
    {
        UpdatePromptWindow window = Build();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Click(window.LaterButton);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.Result.UpdateNow);
            Assert.True(window.Result.CheckAtStartup);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Unticking the box and pressing Later must be honoured. This is the case that rules out
    /// <c>ShowOptOutConfirmationAsync</c>, which persists its opt-out only on the affirmative — a
    /// user who never wants to be asked again is exactly the user who presses Later.
    /// </summary>
    [AvaloniaFact]
    public void UntickingAndPressingLater_StillCarriesThePreference()
    {
        UpdatePromptWindow window = Build();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CheckAtStartupBox.IsChecked = false;
            Click(window.LaterButton);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.Result.UpdateNow);
            Assert.False(window.Result.CheckAtStartup);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Closing without answering — the title bar's X, Alt+F4, the shell closing it — is Later, and
    /// still carries the checkbox.
    /// <para>
    /// Both states are exercised deliberately. <c>StartupUpdatePromptResult</c> is a struct whose
    /// default is <c>(false, false)</c>, so a test that only closed with the box UNTICKED would pass
    /// against a window that recorded nothing at all — which is exactly what an earlier version of
    /// this test did. The ticked case is the one that can tell the difference.
    /// </para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosingWithoutAnswering_IsLaterAndKeepsTheCheckbox(bool checkAtStartup)
    {
        UpdatePromptWindow window = Build();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CheckAtStartupBox.IsChecked = checkAtStartup;

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.Result.UpdateNow);
        Assert.Equal(checkAtStartup, window.Result.CheckAtStartup);
    }

    /// <summary>
    /// "Update now" restarts the app through the updater, so a second click must not queue a second
    /// install. The first answer stands and the buttons go dead.
    /// </summary>
    [AvaloniaFact]
    public void ASecondClick_CannotChangeTheAnswer()
    {
        UpdatePromptWindow window = Build();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Click(window.LaterButton);
            Dispatcher.UIThread.RunJobs();
            Click(window.UpdateNowButton);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.Result.UpdateNow);
            Assert.False(window.UpdateNowButton.IsEnabled);
            Assert.False(window.LaterButton.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Escape is Later. It routes through IsCancel, which raises the button's Click — the same path
    /// the button itself takes, so the checkbox comes with it.
    /// <para>
    /// Both states again: expecting only <c>(false, false)</c> is the struct's own default, so a
    /// single unticked case would pass against an Escape that did nothing at all.
    /// </para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Escape_IsLater(bool checkAtStartup)
    {
        UpdatePromptWindow window = Build();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CheckAtStartupBox.IsChecked = checkAtStartup;
            Press(window, Key.Escape, PhysicalKey.Escape);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.Result.UpdateNow);
            Assert.Equal(checkAtStartup, window.Result.CheckAtStartup);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Focus starts on Later, not on the button that restarts the app. A stray Return on a dialog
    /// nobody read should not install an update; Return still means yes for someone who did, because
    /// "Update now" stays IsDefault.
    /// </summary>
    [AvaloniaFact]
    public void InitialFocus_IsTheSafeButton()
    {
        UpdatePromptWindow window = Build();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.LaterButton.IsFocused);
            Assert.True(window.UpdateNowButton.IsDefault);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheMessage_NamesBothVersions()
    {
        UpdatePromptWindow window = Build();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("1.6.0", window.MessageText.Text, StringComparison.Ordinal);
            Assert.Contains("1.5.0", window.MessageText.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }
}
