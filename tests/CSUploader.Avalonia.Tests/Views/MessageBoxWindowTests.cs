// <copyright file="MessageBoxWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless behavior tests for <see cref="MessageBoxWindow"/> (Phase 4 Task 3): the three modes, the
/// keyboard roles (Reality-check #1 — Avalonia's IsDefault/IsCancel route Enter/Esc through a button's
/// Click but do NOT auto-close, so the explicit Close handlers are what dismiss the window), the outcome
/// mapping, and both completion paths — ownerless <c>Show()</c> + <c>Outcome</c> (what the static
/// helper's null-owner branch reads) and modal <c>ShowDialog&lt;MessageBoxOutcome&gt;</c> (its owner
/// branch). Every shown window is closed in a <c>finally</c> (headless windows are process-global for the
/// session).
/// </summary>
public class MessageBoxWindowTests
{
    // ── Mode plumbing ──

    [AvaloniaFact]
    public void OkMode_ShowsOnlyOkButton_CheckboxHidden()
    {
        var box = new MessageBoxWindow("Something failed.", "Error", MessageBoxMode.Ok);
        try
        {
            box.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(box.OkButton.IsVisible);
            Assert.False(box.YesButton.IsVisible);
            Assert.False(box.NoButton.IsVisible);
            Assert.False(box.DontAskAgainCheck.IsVisible);
        }
        finally
        {
            box.Close();
        }
    }

    [AvaloniaFact]
    public void YesNoMode_ShowsYesAndNo_CheckboxHidden()
    {
        var box = new MessageBoxWindow("Proceed?", "Confirm", MessageBoxMode.YesNo);
        try
        {
            box.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(box.YesButton.IsVisible);
            Assert.True(box.NoButton.IsVisible);
            Assert.False(box.OkButton.IsVisible);
            Assert.False(box.DontAskAgainCheck.IsVisible);
        }
        finally
        {
            box.Close();
        }
    }

    [AvaloniaFact]
    public void OptOutMode_ShowsCheckbox()
    {
        var box = new MessageBoxWindow("Proceed?", "Confirm", MessageBoxMode.YesNoDontAskAgain);
        try
        {
            box.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(box.YesButton.IsVisible);
            Assert.True(box.NoButton.IsVisible);
            Assert.True(box.DontAskAgainCheck.IsVisible);
        }
        finally
        {
            box.Close();
        }
    }

    // ── Keyboard roles + outcome mapping (ownerless Show() + Outcome) ──

    [AvaloniaFact]
    public void YesNo_EnterRoutesToDefaultYes_ConfirmsWithoutOptOut()
    {
        var box = new MessageBoxWindow("Proceed?", "Confirm", MessageBoxMode.YesNo);
        try
        {
            box.Show();
            Dispatcher.UIThread.RunJobs();

            Press(box, Key.Enter, PhysicalKey.Enter); // IsDefault → Yes
            Dispatcher.UIThread.RunJobs();

            Assert.False(box.IsVisible); // the explicit Close handler dismissed it
            Assert.Equal(new MessageBoxOutcome(Confirmed: true, DontAskAgain: false), box.Outcome);
        }
        finally
        {
            box.Close();
        }
    }

    [AvaloniaFact]
    public void OptOut_TickedThenEnter_ConfirmsWithOptOut()
    {
        var box = new MessageBoxWindow("Proceed?", "Confirm", MessageBoxMode.YesNoDontAskAgain);
        try
        {
            box.Show();
            Dispatcher.UIThread.RunJobs();

            box.DontAskAgainCheck.IsChecked = true;
            Press(box, Key.Enter, PhysicalKey.Enter); // IsDefault → Yes, carrying the ticked box

            Dispatcher.UIThread.RunJobs();

            Assert.False(box.IsVisible);
            Assert.Equal(new MessageBoxOutcome(Confirmed: true, DontAskAgain: true), box.Outcome);
        }
        finally
        {
            box.Close();
        }
    }

    [AvaloniaFact]
    public void YesNo_EscRoutesToCancelNo_NotConfirmed()
    {
        var box = new MessageBoxWindow("Proceed?", "Confirm", MessageBoxMode.YesNo);
        try
        {
            box.Show();
            Dispatcher.UIThread.RunJobs();

            Press(box, Key.Escape, PhysicalKey.Escape); // IsCancel → No
            Dispatcher.UIThread.RunJobs();

            Assert.False(box.IsVisible);
            Assert.Equal(new MessageBoxOutcome(Confirmed: false, DontAskAgain: false), box.Outcome);
        }
        finally
        {
            box.Close();
        }
    }

    [AvaloniaFact]
    public void Ok_EnterDismisses_Confirmed()
    {
        var box = new MessageBoxWindow("Done.", "Notice", MessageBoxMode.Ok);
        try
        {
            box.Show();
            Dispatcher.UIThread.RunJobs();

            Press(box, Key.Enter, PhysicalKey.Enter); // OK is IsDefault + IsCancel
            Dispatcher.UIThread.RunJobs();

            Assert.False(box.IsVisible);
            Assert.Equal(new MessageBoxOutcome(Confirmed: true, DontAskAgain: false), box.Outcome);
        }
        finally
        {
            box.Close();
        }
    }

    [AvaloniaFact]
    public void WindowClosedWithoutButton_YieldsNotConfirmed()
    {
        // The window-X path: Close() with no result runs no handler, so Outcome keeps its (false, false)
        // default — the WPF DialogResult != true path. The modal ShowDialog<T> equivalent returns default.
        var box = new MessageBoxWindow("Proceed?", "Confirm", MessageBoxMode.YesNo);
        box.Show();
        Dispatcher.UIThread.RunJobs();

        box.Close(); // simulates the window-X / no completion call
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new MessageBoxOutcome(Confirmed: false, DontAskAgain: false), box.Outcome);
    }

    // The non-obsolete KeyPress overload (physical key + key symbol). Enter/Esc carry the logical Key
    // the default/cancel button handlers listen for; the physical key + null symbol satisfy the API.
    private static void Press(Window window, Key key, PhysicalKey physical)
        => window.KeyPress(key, RawInputModifiers.None, physical, null);

    // ── Modal path: ShowDialog<MessageBoxOutcome> returns the button outcome ──

    [AvaloniaFact]
    public async Task ShowDialog_ModalEnterYes_ReturnsConfirmedOutcome()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var box = new MessageBoxWindow("Proceed?", "Confirm", MessageBoxMode.YesNo);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            // ShowDialog returns immediately (Avalonia modality is non-blocking); pump, drive, then await
            // the already-completed task — the Reality-check #8 interplay, verified here.
            Task<MessageBoxOutcome> dialog = box.ShowDialog<MessageBoxOutcome>(owner);
            Dispatcher.UIThread.RunJobs();
            Press(box, Key.Enter, PhysicalKey.Enter);
            Dispatcher.UIThread.RunJobs();

            MessageBoxOutcome outcome = await dialog;
            Assert.Equal(new MessageBoxOutcome(Confirmed: true, DontAskAgain: false), outcome);
            Assert.Equal(outcome, box.Outcome); // the modal result equals the Outcome property
        }
        finally
        {
            box.Close();
            owner.Close();
        }
    }
}
