// <copyright file="MessageBoxWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CSUploader.Views;

/// <summary>
/// The Avalonia head's custom message box (design: MessageBox.Show sites → a small custom
/// message-box window, no MsBox.Avalonia dependency). One window with three modes replaces both WPF
/// message surfaces: <see cref="MessageBoxMode.Ok"/> is the WPF <c>MessageBox.Show(OK)</c>
/// notification, <see cref="MessageBoxMode.YesNo"/> the <c>MessageBox.Show(YesNo)</c> confirmation, and
/// <see cref="MessageBoxMode.YesNoDontAskAgain"/> IS the <c>ConfirmationDialog</c> port (message plus a
/// "Don't ask again" checkbox). Consumed through the static composition helpers below, which own the
/// null-owner policy.
/// </summary>
/// <remarks>
/// WPF <c>MessageBox.Show</c> drew system Error/Question icons; this box shows none (the
/// ConfirmationDialog styling for every mode) — a deliberate "close and consistent" deviation, noted at
/// the phase gate. Avalonia's <c>IsDefault</c>/<c>IsCancel</c> route Enter/Esc through a button's
/// <c>Click</c> but do NOT auto-close the window (unlike WPF), so each completion goes through an
/// explicit <see cref="Complete"/> → <see cref="Window.Close(object?)"/> (port rule 7).
/// </remarks>
public partial class MessageBoxWindow : Window
{
    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses
    // the 3-arg overload. Defaults to an empty Ok box.
    public MessageBoxWindow()
        : this(string.Empty, string.Empty, MessageBoxMode.Ok)
    {
    }

    internal MessageBoxWindow(string message, string title, MessageBoxMode mode)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        switch (mode)
        {
            case MessageBoxMode.Ok:
                // A single OK button that is BOTH default (Enter) and cancel (Esc) — parity with the WPF
                // MessageBox.Show(OK), where either key dismisses.
                OkButton.IsVisible = true;
                OkButton.IsDefault = true;
                OkButton.IsCancel = true;
                break;

            case MessageBoxMode.YesNo:
            case MessageBoxMode.YesNoDontAskAgain:
                YesButton.IsVisible = true;
                NoButton.IsVisible = true;
                YesButton.IsDefault = true; // Enter → Yes (ConfirmationDialog.xaml:37)
                NoButton.IsCancel = true;   // Esc   → No  (ConfirmationDialog.xaml:38)
                DontAskAgainCheck.IsVisible = mode == MessageBoxMode.YesNoDontAskAgain;
                break;
        }
    }

    /// <summary>
    /// The outcome, set by every button handler before <see cref="Window.Close(object?)"/>. The
    /// ownerless <c>Show()</c> + await <c>Closed</c> path reads this (nothing else carries the result
    /// there); the modal path receives the same value as the <c>ShowDialog&lt;MessageBoxOutcome&gt;</c>
    /// result. The initializer default — for a window-X close, which never runs a handler — is
    /// <c>(false, false)</c> = not confirmed, matching the WPF <c>DialogResult != true</c> path.
    /// </summary>
    internal MessageBoxOutcome Outcome { get; private set; }

    private void Ok_Click(object? sender, RoutedEventArgs e)
        => Complete(new MessageBoxOutcome(Confirmed: true, DontAskAgain: false));

    private void Yes_Click(object? sender, RoutedEventArgs e)
        => Complete(new MessageBoxOutcome(Confirmed: true, DontAskAgain: DontAskAgainCheck.IsChecked == true));

    // Close(new MessageBoxOutcome(false, false)) — the same value as Close(default), but stating the
    // intent: No is an explicit "not confirmed", not an incidental default.
    private void No_Click(object? sender, RoutedEventArgs e)
        => Complete(new MessageBoxOutcome(Confirmed: false, DontAskAgain: false));

    private void Complete(MessageBoxOutcome outcome)
    {
        Outcome = outcome;
        Close(outcome);
    }

    /// <summary>Shows an OK notification. Returns when the box is dismissed.</summary>
    internal static async Task ShowErrorAsync(Window? owner, string message, string title)
        => await ShowCoreAsync(owner, message, title, MessageBoxMode.Ok);

    /// <summary>Shows a Yes/No confirmation. Returns whether the user chose Yes.</summary>
    internal static async Task<bool> ShowConfirmationAsync(Window? owner, string message, string title)
        => (await ShowCoreAsync(owner, message, title, MessageBoxMode.YesNo)).Confirmed;

    /// <summary>Shows the opt-out confirmation (Yes/No + "Don't ask again"). Returns the full outcome.</summary>
    internal static Task<MessageBoxOutcome> ShowOptOutAsync(Window? owner, string message, string title)
        => ShowCoreAsync(owner, message, title, MessageBoxMode.YesNoDontAskAgain);

    // Composition helper: builds the window for the requested mode and hands it to the show seam below.
    private static Task<MessageBoxOutcome> ShowCoreAsync(Window? owner, string message, string title, MessageBoxMode mode)
        => ShowCoreAsync(owner, new MessageBoxWindow(message, title, mode));

    // The show/await seam, split out (InternalsVisibleTo → CSUploader.Avalonia.Tests) so a headless test can
    // construct and drive the real window through either branch. Owns the null-owner policy (design:
    // "ownerless Show()+await Closed for the message box"): a non-null owner → modal ShowDialog<T> (its
    // result carries the outcome, and ShowInTaskbar stays the XAML-default False so the box rides its
    // parent); a null owner (tray-hidden main, or headless with no desktop lifetime) → an ownerless Show()
    // with a taskbar entry (re-findable while main is hidden) and await the Closed event, then read the
    // Outcome property. Never reveal the tray-hidden main window for a notification.
    internal static async Task<MessageBoxOutcome> ShowCoreAsync(Window? owner, MessageBoxWindow window)
    {
        if (owner is not null)
        {
            return await window.ShowDialog<MessageBoxOutcome>(owner);
        }

        window.ShowInTaskbar = true;
        TaskCompletionSource completion = new();
        window.Closed += (_, _) => completion.TrySetResult();
        window.Show();
        await completion.Task;
        return window.Outcome;
    }
}

/// <summary>The three message-box shapes: notification, confirmation, and opt-out confirmation.</summary>
internal enum MessageBoxMode
{
    Ok,
    YesNo,
    YesNoDontAskAgain,
}

/// <summary>
/// Outcome of a <see cref="MessageBoxWindow"/>: whether the user confirmed, and whether they ticked
/// "Don't ask again" (only meaningful in the opt-out mode, and only when <see cref="Confirmed"/>).
/// </summary>
internal readonly record struct MessageBoxOutcome(bool Confirmed, bool DontAskAgain);
