// <copyright file="MessageBoxWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CSUploader.Views;
using static CSUploader.Tests.Avalonia.HeadlessInput;

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

    // ── ShowCoreAsync seam: the split show/await half, driven through both owner branches ──

    [AvaloniaFact]
    public async Task ShowCoreAsync_NullOwner_ShowsOwnerlessWithTaskbar_EnterCompletes()
    {
        // The ownerless branch (tray-hidden main / headless): ShowCoreAsync shows the box itself, flips
        // ShowInTaskbar on so a tray-hidden user can re-find it, and completes when the box closes. Enter
        // routes to the Ok box's default button (OK is IsDefault+IsCancel) → Ok_Click → (true, false).
        var box = new MessageBoxWindow("Done.", "Notice", MessageBoxMode.Ok);
        try
        {
            Task<MessageBoxOutcome> pending = MessageBoxWindow.ShowCoreAsync(null, box);
            Dispatcher.UIThread.RunJobs();

            Assert.True(box.ShowInTaskbar); // ownerless branch gave it a taskbar entry
            Assert.True(box.IsVisible);

            Press(box, Key.Enter, PhysicalKey.Enter);
            Dispatcher.UIThread.RunJobs();

            MessageBoxOutcome outcome = await pending;
            Assert.Equal(new MessageBoxOutcome(Confirmed: true, DontAskAgain: false), outcome);
        }
        finally
        {
            box.Close();
        }
    }

    [AvaloniaFact]
    public async Task ShowCoreAsync_WithOwner_ShowsModal_KeepsTaskbarFalse_EnterCompletes()
    {
        // The owned branch: ShowCoreAsync goes through modal ShowDialog<T>, whose result carries the
        // outcome, and leaves ShowInTaskbar at the XAML-default False (the box rides its parent). Same
        // Enter drive, same outcome — only the branch differs.
        var owner = new Window { Width = 200, Height = 200 };
        var box = new MessageBoxWindow("Done.", "Notice", MessageBoxMode.Ok);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<MessageBoxOutcome> pending = MessageBoxWindow.ShowCoreAsync(owner, box);
            Dispatcher.UIThread.RunJobs();

            Assert.False(box.ShowInTaskbar); // owned/modal path keeps the XAML default

            Press(box, Key.Enter, PhysicalKey.Enter);
            Dispatcher.UIThread.RunJobs();

            MessageBoxOutcome outcome = await pending;
            Assert.Equal(new MessageBoxOutcome(Confirmed: true, DontAskAgain: false), outcome);
            Assert.Equal(outcome, box.Outcome); // the modal result equals the Outcome property
        }
        finally
        {
            box.Close();
            owner.Close();
        }
    }

    // ── Per-type icons (Phase 9 add-on: WPF-parity system-icon glyphs) ──
    // WPF drew OS system icons per MessageBoxImage; the custom box reproduces them under Fluent as themed
    // MDI glyphs. These are [Fact]s (not [Theory]s) because the internal MessageBoxMode/MessageBoxIcon enums
    // cannot be public-method parameters — the cases run through private local helpers instead. The map pins
    // each type -> its (window-local geometry key, theme brush key); the window facts pin that the ctor
    // wires IconKind + the icon's visibility + that both resources actually resolve.

    [Fact]
    public void ResolveIconResources_MapsEachTypeToItsGlyphAndBrush()
    {
        AssertMap(MessageBoxIcon.None, null, null);
        AssertMap(MessageBoxIcon.Information, "MessageBoxInformationGeometry", "InfoAccentBrush");
        AssertMap(MessageBoxIcon.Warning, "MessageBoxWarningGeometry", "WarningBrush");
        AssertMap(MessageBoxIcon.Error, "MessageBoxErrorGeometry", "ErrorBrush");
        AssertMap(MessageBoxIcon.Question, "MessageBoxQuestionGeometry", "AccentBrush");

        static void AssertMap(MessageBoxIcon icon, string? geometryKey, string? brushKey)
        {
            (string? geometry, string? brush) = MessageBoxWindow.ResolveIconResources(icon);
            Assert.Equal(geometryKey, geometry);
            Assert.Equal(brushKey, brush);
        }
    }

    [AvaloniaFact]
    public void Ctor_WiresIconKindAndVisibility_PerType()
    {
        // The full per-type mapping at the window level: each notification/confirmation shape the helpers
        // build carries the right glyph, and only the opt-out box (None) is icon-less (WPF ConfirmationDialog).
        AssertIcon(MessageBoxMode.Ok, MessageBoxIcon.Error, iconVisible: true);
        AssertIcon(MessageBoxMode.Ok, MessageBoxIcon.Warning, iconVisible: true);
        AssertIcon(MessageBoxMode.Ok, MessageBoxIcon.Information, iconVisible: true);
        AssertIcon(MessageBoxMode.YesNo, MessageBoxIcon.Question, iconVisible: true);
        AssertIcon(MessageBoxMode.YesNoDontAskAgain, MessageBoxIcon.None, iconVisible: false);

        static void AssertIcon(MessageBoxMode mode, MessageBoxIcon icon, bool iconVisible)
        {
            var box = new MessageBoxWindow("Something happened.", "Notice", mode, icon);
            try
            {
                box.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(icon, box.IconKind);
                Assert.Equal(iconVisible, box.IconGlyph.IsVisible);
            }
            finally
            {
                box.Close();
            }
        }
    }

    [AvaloniaFact]
    public void VisibleIcons_ResolveGeometryAndBrush()
    {
        // A key typo would leave Data/Fill null (an invisible glyph) while the string map above still passed
        // — so drive the real resource lookup: the ctor binds both, and the window-local geometry plus the
        // app-wide theme brush must resolve on the constructed window.
        foreach (MessageBoxIcon icon in new[] { MessageBoxIcon.Error, MessageBoxIcon.Warning, MessageBoxIcon.Information, MessageBoxIcon.Question })
        {
            (string? geometryKey, _) = MessageBoxWindow.ResolveIconResources(icon);
            var box = new MessageBoxWindow("Something happened.", "Notice", MessageBoxMode.Ok, icon);
            try
            {
                box.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.True(box.TryFindResource(geometryKey!, out object? geometry), $"geometry key did not resolve: {geometryKey}");
                Assert.IsAssignableFrom<Geometry>(geometry);
                Assert.NotNull(box.IconGlyph.Data); // ctor bound the resolved geometry
                Assert.NotNull(box.IconGlyph.Fill); // ctor bound the resolved theme brush
            }
            finally
            {
                box.Close();
            }
        }
    }
}
