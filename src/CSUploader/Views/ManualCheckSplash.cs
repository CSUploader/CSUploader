// <copyright file="ManualCheckSplash.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Update;

namespace CSUploader.Views;

/// <summary>
/// Shows the startup <see cref="SplashWindow"/> over an owner while a manual update check runs —
/// Help → Check for Updates used to await invisibly, which read as the menu item doing nothing.
/// A static helper rather than handler-inline so the choreography is testable with a
/// completion-source check and a plain owner window.
/// </summary>
internal static class ManualCheckSplash
{
    /// <summary>
    /// Waits for <paramref name="check"/> behind a modal splash and returns the result to report —
    /// or null when the wait was dismissed: the user closed the splash, or the owner went
    /// invisible under it (the close-to-tray reroute hides the main window even while a modal is
    /// up). Either way the caller shows nothing — a result dialog needs a visible owner — and the
    /// check (which Velopack cannot cancel) finishes elsewhere with its normal background
    /// reporting.
    /// </summary>
    /// <param name="owner">The window the splash is centered on and modal to.</param>
    /// <param name="check">The in-flight check; single-flight joining is the caller's concern.</param>
    /// <param name="patience">How long to wait before answering that the check is still running —
    /// mapped to the same localized Failed result the pre-splash flow reported.</param>
    /// <param name="minimumDisplay">How long the splash stays up even when the answer beats it —
    /// an instantly-dismissed window reads as a glitch, not as "checked".</param>
    internal static async Task<UpdateCheckResult?> WaitAsync(
        Window owner, Task<UpdateCheckResult> check, TimeSpan patience, TimeSpan minimumDisplay)
    {
        SplashWindow splash = new()
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,

            // The startup splash wants its taskbar entry and caption buttons (it IS the app at
            // that point); this one is an owned modal over a window that already has both. A
            // second identical "CSUploader" taskbar entry would be clutter — and with no entry,
            // minimizing must be off the table too: a minimized modal with a modality-disabled
            // owner and no taskbar presence is an app with no clickable window at all.
            ShowInTaskbar = false,
            CanMinimize = false,
        };

        // ShowDialog's task doubles as the "user closed it" signal: until this helper calls
        // Close() itself — which it does only in the finally below, after every return value is
        // already decided — the ONLY way that task completes is the user dismissing the window.
        Task dialog = splash.ShowDialog(owner);
        Task floor = Task.Delay(minimumDisplay);
        Task<UpdateCheckResult> bounded = check.WaitAsync(patience);

        // The owner hiding is the third way the wait ends. Close-to-tray reaches the main window
        // even while this modal is up (the taskbar thumbnail's Close), and its reroute Hide()s the
        // owner rather than closing it — which completes nothing this method awaits, so without
        // this the splash would stand stranded over an invisible window until the patience ran
        // out, and the result box would then throw on its non-visible owner.
        TaskCompletionSource ownerHidden = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOwnerPropertyChanged(object? _, global::Avalonia.AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.IsVisibleProperty && !owner.IsVisible)
            {
                ownerHidden.TrySetResult();
            }
        }

        owner.PropertyChanged += OnOwnerPropertyChanged;

        // Observed up front because the dismissal paths below return WITHOUT awaiting bounded,
        // and an abandoned WaitAsync faults with TimeoutException when the patience lapses — an
        // unobserved-exception mine for the day the app wires UnobservedTaskException. A task
        // that IS awaited later is simply observed twice, which costs nothing.
        _ = bounded.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        bool Dismissed() => dialog.IsCompleted || ownerHidden.Task.IsCompleted;

        try
        {
            await Task.WhenAny(bounded, dialog, ownerHidden.Task);
            if (Dismissed())
            {
                return null; // the wait was dismissed; report nothing
            }

            UpdateCheckResult result;
            try
            {
                result = await bounded;
            }
            catch (TimeoutException)
            {
                result = UpdateCheckResult.Failed(Localizer.Instance["Main_CheckForUpdates_StillRunning"]);
            }

            // The floor is raced, not merely awaited: a dismissal during it must end the wait
            // there and then — the splash is already gone, so padding out the rest of the floor
            // would only keep this handler alive into territory a second click can reach.
            await Task.WhenAny(floor, dialog, ownerHidden.Task);
            return Dismissed() ? null : result;
        }
        finally
        {
            owner.PropertyChanged -= OnOwnerPropertyChanged;

            // Also the guard for a check task that faults with something other than a timeout: the
            // exception propagates to the caller, but a modal splash must never outlive the wait.
            if (!dialog.IsCompleted)
            {
                splash.Close();
            }

            await dialog;
        }
    }
}
