// <copyright file="SimpleDialogTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless behavior tests for the three Phase 4 Task 5 text dialogs (<see cref="SpeedLimitDialog"/>,
/// <see cref="ProxyTextDialog"/>, <see cref="ErrorDetailsWindow"/>). The load-bearing checks are the
/// result-contract mappings: SpeedLimit's two-level nullability (Cancel → null, OK-valid → the limit,
/// OK-empty/Clear → a non-null selection carrying a null limit — the boxed-struct disambiguation the
/// port depends on), ProxyText's edit/read-only modes, and ErrorDetails' text/Copy. Every shown window
/// is closed in a <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class SimpleDialogTests
{
    // ── SpeedLimitDialog: outcome mapping through ShowDialog<SpeedLimitSelection?> ──

    [AvaloniaFact]
    public async Task SpeedLimit_ValidInput_Ok_ReturnsLimit()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new SpeedLimitDialog(512);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<SpeedLimitSelection?> dialog = dlg.ShowDialog<SpeedLimitSelection?>(owner);
            Dispatcher.UIThread.RunJobs();
            Click(dlg.OkButton); // "512" is valid → Close(new SpeedLimitSelection(512))
            Dispatcher.UIThread.RunJobs();

            SpeedLimitSelection? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal(512, result!.Value.LimitKBps);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task SpeedLimit_EmptyInput_Ok_ReturnsClearedSelection()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new SpeedLimitDialog(null); // LimitBox starts empty
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<SpeedLimitSelection?> dialog = dlg.ShowDialog<SpeedLimitSelection?>(owner);
            Dispatcher.UIThread.RunJobs();
            Click(dlg.OkButton); // empty → Close(new SpeedLimitSelection(null)) — the "cleared" outcome
            Dispatcher.UIThread.RunJobs();

            SpeedLimitSelection? result = await dialog;
            Assert.NotNull(result); // NOT cancelled: a selection was made
            Assert.Null(result!.Value.LimitKBps); // ...and it cleared the limit
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task SpeedLimit_Clear_ReturnsClearedSelection()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new SpeedLimitDialog(512); // even with a seeded limit, Clear reverts it
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<SpeedLimitSelection?> dialog = dlg.ShowDialog<SpeedLimitSelection?>(owner);
            Dispatcher.UIThread.RunJobs();
            Click(dlg.ClearButton); // Close(new SpeedLimitSelection(null))
            Dispatcher.UIThread.RunJobs();

            SpeedLimitSelection? result = await dialog;
            Assert.NotNull(result);
            Assert.Null(result!.Value.LimitKBps);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task SpeedLimit_CancelViaEsc_ReturnsNull()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new SpeedLimitDialog(512);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<SpeedLimitSelection?> dialog = dlg.ShowDialog<SpeedLimitSelection?>(owner);
            Dispatcher.UIThread.RunJobs();
            Press(dlg, Key.Escape, PhysicalKey.Escape); // IsCancel → CancelButton_Click → Close(null)
            Dispatcher.UIThread.RunJobs();

            SpeedLimitSelection? result = await dialog;
            Assert.Null(result); // cancelled — leave limits untouched (distinct from the cleared cases)
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void SpeedLimit_InvalidInput_KeepsDialogOpen_AndShowsMessageBox()
    {
        var dlg = new SpeedLimitDialog(null);
        try
        {
            dlg.Show(); // shown non-modally so the validation box can own over it (owner = this)
            Dispatcher.UIThread.RunJobs();

            dlg.LimitBox.Text = "abc";
            Click(dlg.OkButton); // invalid → ShowErrorAsync(this); no Close
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible); // the dialog stayed open (WPF validation parity)

            MessageBoxWindow? box = dlg.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault();
            Assert.NotNull(box); // a validation message box appeared, owned by the dialog

            box!.Close(); // dismiss it
            Dispatcher.UIThread.RunJobs();
            Assert.True(dlg.IsVisible); // still open after dismissing the warning
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── ProxyTextDialog: edit vs read-only ──

    [AvaloniaFact]
    public async Task ProxyText_Editable_Import_ReturnsEditedText()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new ProxyTextDialog("Import", "desc", "seed", readOnly: false);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<string?> dialog = dlg.ShowDialog<string?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.BodyBox.Text = "127.0.0.1:8080\n10.0.0.1:1080";
            Click(dlg.OkButton); // Import → Close(BodyBox.Text)
            Dispatcher.UIThread.RunJobs();

            string? result = await dialog;
            Assert.Equal("127.0.0.1:8080\n10.0.0.1:1080", result);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task ProxyText_Editable_Cancel_ReturnsNull()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new ProxyTextDialog("Import", "desc", "seed", readOnly: false);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<string?> dialog = dlg.ShowDialog<string?>(owner);
            Dispatcher.UIThread.RunJobs();
            Press(dlg, Key.Escape, PhysicalKey.Escape); // IsCancel → Cancel_Click → Close(null)
            Dispatcher.UIThread.RunJobs();

            string? result = await dialog;
            Assert.Null(result);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void ProxyText_ReadOnly_HidesImport_ShowsCopy_RelabelsClose()
    {
        var dlg = new ProxyTextDialog("Export", "desc", "body text", readOnly: true);
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.False(dlg.OkButton.IsVisible); // Import hidden in export mode
            Assert.True(dlg.CopyButton.IsVisible); // Copy shown
            Assert.True(dlg.BodyBox.IsReadOnly);
            Assert.Equal(Localizer.Instance["Common_Close"], dlg.CancelButton.Content); // relabel deviation
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── ErrorDetailsWindow: text lands, Copy is safe ──

    [AvaloniaFact]
    public void ErrorDetails_CtorText_LandsInBox()
    {
        var dlg = new ErrorDetailsWindow("the full error text\nsecond line");
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("the full error text\nsecond line", dlg.DetailBox.Text);
            Assert.True(dlg.DetailBox.IsReadOnly);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void ErrorDetails_Copy_DoesNotThrow()
    {
        var dlg = new ErrorDetailsWindow("copy me");
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            // The Copy handler swallows clipboard failures (headless has no real clipboard); invoking it
            // must not surface an exception.
            Exception? ex = Record.Exception(() => Click(dlg.CopyButton));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(ex);
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── CloseActionDialog: outcome mapping through ShowDialog<CloseActionChoice?> (Task 6) ──

    [AvaloniaFact]
    public async Task CloseAction_Minimize_DefaultChecked_RemembersMinimizeToTray()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new CloseActionDialog();
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<CloseActionChoice?> dialog = dlg.ShowDialog<CloseActionChoice?>(owner);
            Dispatcher.UIThread.RunJobs();
            Click(dlg.MinimizeButton); // "Remember" defaults checked (WPF parity) → (MinimizeToTray, true)
            Dispatcher.UIThread.RunJobs();

            CloseActionChoice? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal(CloseAction.MinimizeToTray, result!.Value.Action);
            Assert.True(result.Value.Remember);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloseAction_Exit_Unchecked_ReturnsExitNotRemembered()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new CloseActionDialog();
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<CloseActionChoice?> dialog = dlg.ShowDialog<CloseActionChoice?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.RememberCheck.IsChecked = false; // untick before choosing → remember flag plumbs false
            Click(dlg.ExitButton); // → (Exit, false)
            Dispatcher.UIThread.RunJobs();

            CloseActionChoice? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal(CloseAction.Exit, result!.Value.Action);
            Assert.False(result.Value.Remember);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloseAction_Cancel_ReturnsNull()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new CloseActionDialog();
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<CloseActionChoice?> dialog = dlg.ShowDialog<CloseActionChoice?>(owner);
            Dispatcher.UIThread.RunJobs();
            Click(dlg.CancelButton); // Cancel_Click → Close(null): keep the window open, setting unchanged
            Dispatcher.UIThread.RunJobs();

            CloseActionChoice? result = await dialog;
            Assert.Null(result); // cancelled — distinct from any minimize/exit choice
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    // ── AboutWindow: opens, version line renders, OK closes ──

    [AvaloniaFact]
    public void About_Opens_ShowsVersion_OkCloses()
    {
        var dlg = new AboutWindow();
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(dlg.VersionText.Text)); // version line rendered (1.0.0 in Phase 4)

            Click(dlg.OkButton); // WPF's OK had NO handler; the port adds an explicit Close() (rule 7 gotcha)
            Dispatcher.UIThread.RunJobs();
            Assert.False(dlg.IsVisible); // OK closed it
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── input helpers (mirroring MessageBoxWindowTests) ──

    // Raises the Click routed event the real pointer/keyboard click raises, invoking the XAML-wired
    // handler deterministically — no reliance on hit-testing a small button in the headless surface.
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // The non-obsolete KeyPress overload (logical key + physical key). Esc routes to the IsCancel button's
    // Click on 11.3.18 (verified for the message box in MessageBoxWindowTests; reused here).
    private static void Press(Window window, Key key, PhysicalKey physical)
        => window.KeyPress(key, RawInputModifiers.None, physical, null);
}
