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
    /// or null when the user closed the splash themselves, which means "stop waiting": the caller
    /// shows nothing, and the check (which Velopack cannot cancel) finishes elsewhere and still
    /// publishes wherever its results already go.
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

            // The startup splash wants its taskbar entry (it IS the app at that point); this one
            // is an owned modal over a window that already has one — a second identical
            // "CSUploader" entry for the duration of the check would just be clutter.
            ShowInTaskbar = false,
        };

        // ShowDialog's task doubles as the "user closed it" signal: until this helper calls
        // Close() itself — which it does only in the finally below, after every return value is
        // already decided — the ONLY way that task completes is the user dismissing the window.
        Task dialog = splash.ShowDialog(owner);
        Task floor = Task.Delay(minimumDisplay);
        Task<UpdateCheckResult> bounded = check.WaitAsync(patience);

        // Observed up front because the user-close paths below return WITHOUT awaiting bounded,
        // and an abandoned WaitAsync faults with TimeoutException when the patience lapses — an
        // unobserved-exception mine for the day the app wires UnobservedTaskException. A task
        // that IS awaited later is simply observed twice, which costs nothing.
        _ = bounded.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await Task.WhenAny(bounded, dialog);
            if (dialog.IsCompleted)
            {
                return null; // the user dismissed the wait; report nothing
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

            await floor;
            return dialog.IsCompleted ? null : result; // closed while the floor padded a fast answer
        }
        finally
        {
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
