// <copyright file="MessageBoxWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Path = Avalonia.Controls.Shapes.Path; // disambiguate from System.IO.Path (implicit usings)

namespace CSUploader.Views;

/// <summary>
/// The Avalonia head's custom message box (design: MessageBox.Show sites → a small custom
/// message-box window, no MsBox.Avalonia dependency). One window with three modes replaces both WPF
/// message surfaces: <see cref="MessageBoxMode.Ok"/> is the WPF <c>MessageBox.Show(OK)</c>
/// notification, <see cref="MessageBoxMode.YesNo"/> the <c>MessageBox.Show(YesNo)</c> confirmation, and
/// <see cref="MessageBoxMode.YesNoDontAskAgain"/> IS the <c>ConfirmationDialog</c> port (message plus a
/// "Don't ask again" checkbox). Consumed through the static composition helpers below, which own the
/// null-owner policy and pick each shape's <see cref="MessageBoxIcon"/>.
/// </summary>
/// <remarks>
/// WPF <c>MessageBox.Show</c> drew OS shell icons per <c>MessageBoxImage</c>; this box reproduces them
/// under Fluent as themed Material-Design glyphs (Phase 9, resolving the phase-gate deviation): the Ok
/// notification carries Error / Warning / Information per call site, the Yes/No confirmation carries
/// Question, and the opt-out box carries none (WPF's <c>ConfirmationDialog</c> had no icon). Avalonia's
/// <c>IsDefault</c>/<c>IsCancel</c> route Enter/Esc through a button's <c>Click</c> but do NOT auto-close
/// the window (unlike WPF), so each completion goes through an explicit <see cref="Complete"/> →
/// <see cref="Window.Close(object?)"/> (port rule 7).
/// </remarks>
public partial class MessageBoxWindow : Window
{
    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses
    // the mode/icon overload. Defaults to an empty, icon-less Ok box.
    public MessageBoxWindow()
        : this(string.Empty, string.Empty, MessageBoxMode.Ok)
    {
    }

    internal MessageBoxWindow(string message, string title, MessageBoxMode mode, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        IconKind = icon;

        // Reproduce the WPF system icon: bind the window-local glyph + a theme brush via DynamicResource so
        // the tint tracks light/dark. None leaves the Path hidden, collapsing its Auto column to 0 width so
        // the message is flush-left (the pre-Phase-9 look, and the WPF ConfirmationDialog's no-icon layout).
        (string? geometryKey, string? brushKey) = ResolveIconResources(icon);
        if (geometryKey is not null)
        {
            IconGlyph.IsVisible = true;
            IconGlyph[!Path.DataProperty] = new DynamicResourceExtension(geometryKey);
            IconGlyph[!Shape.FillProperty] = new DynamicResourceExtension(brushKey!);
        }

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

    /// <summary>The severity glyph this box carries, set at construction (None = no icon).</summary>
    internal MessageBoxIcon IconKind { get; }

    /// <summary>
    /// The outcome, set by every button handler before <see cref="Window.Close(object?)"/>. The
    /// ownerless <c>Show()</c> + await <c>Closed</c> path reads this (nothing else carries the result
    /// there); the modal path receives the same value as the <c>ShowDialog&lt;MessageBoxOutcome&gt;</c>
    /// result. The initializer default — for a window-X close, which never runs a handler — is
    /// <c>(false, false)</c> = not confirmed, matching the WPF <c>DialogResult != true</c> path.
    /// </summary>
    internal MessageBoxOutcome Outcome { get; private set; }

    /// <summary>
    /// Maps a <see cref="MessageBoxIcon"/> to its window-local glyph geometry key and the theme brush
    /// key that tints it, or <c>(null, null)</c> for <see cref="MessageBoxIcon.None"/>. Pure so the
    /// per-type icon choice is pinned by a headless test without resolving resources.
    /// </summary>
    internal static (string? Geometry, string? Brush) ResolveIconResources(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => ("MessageBoxErrorGeometry", "ErrorBrush"),
        MessageBoxIcon.Warning => ("MessageBoxWarningGeometry", "WarningBrush"),
        MessageBoxIcon.Information => ("MessageBoxInformationGeometry", "InfoAccentBrush"),
        MessageBoxIcon.Question => ("MessageBoxQuestionGeometry", "AccentBrush"),
        _ => (null, null),
    };

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

    /// <summary>Shows an OK error notification (red close-circle glyph). Returns when dismissed.</summary>
    internal static async Task ShowErrorAsync(Window? owner, string message, string title)
        => await ShowCoreAsync(owner, message, title, MessageBoxMode.Ok, MessageBoxIcon.Error);

    /// <summary>Shows an OK warning notification (amber alert glyph) — the WPF validation-warning boxes.</summary>
    internal static async Task ShowWarningAsync(Window? owner, string message, string title)
        => await ShowCoreAsync(owner, message, title, MessageBoxMode.Ok, MessageBoxIcon.Warning);

    /// <summary>Shows an OK information notification (blue info glyph) — e.g. the update-check result.</summary>
    internal static async Task ShowInformationAsync(Window? owner, string message, string title)
        => await ShowCoreAsync(owner, message, title, MessageBoxMode.Ok, MessageBoxIcon.Information);

    /// <summary>Shows a Yes/No confirmation (blue question glyph). Returns whether the user chose Yes.</summary>
    internal static async Task<bool> ShowConfirmationAsync(Window? owner, string message, string title)
        => (await ShowCoreAsync(owner, message, title, MessageBoxMode.YesNo, MessageBoxIcon.Question)).Confirmed;

    /// <summary>Shows the opt-out confirmation (Yes/No + "Don't ask again", no icon). Returns the outcome.</summary>
    internal static Task<MessageBoxOutcome> ShowOptOutAsync(Window? owner, string message, string title)
        => ShowCoreAsync(owner, message, title, MessageBoxMode.YesNoDontAskAgain, MessageBoxIcon.None);

    // Composition helper: builds the window for the requested mode + icon and hands it to the show seam below.
    private static Task<MessageBoxOutcome> ShowCoreAsync(Window? owner, string message, string title, MessageBoxMode mode, MessageBoxIcon icon)
        => ShowCoreAsync(owner, new MessageBoxWindow(message, title, mode, icon));

    // The show/await seam, split out (InternalsVisibleTo → CSUploader.Tests) so a headless test can
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
/// The severity glyph a <see cref="MessageBoxWindow"/> shows, reproducing WPF's <c>MessageBoxImage</c>
/// system icons under Fluent. <see cref="None"/> shows no icon (the WPF <c>ConfirmationDialog</c> look).
/// </summary>
internal enum MessageBoxIcon
{
    None,
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>
/// Outcome of a <see cref="MessageBoxWindow"/>: whether the user confirmed, and whether they ticked
/// "Don't ask again" (only meaningful in the opt-out mode, and only when <see cref="Confirmed"/>).
/// </summary>
internal readonly record struct MessageBoxOutcome(bool Confirmed, bool DontAskAgain);
