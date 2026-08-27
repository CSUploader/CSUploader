// <copyright file="ManualCheckSplashTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Update;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// The Help → Check for Updates splash choreography: the same startup splash, shown over the main
/// window while the manual check runs, so the menu item visibly does something. The choreography
/// is a static helper precisely so these tests can drive it with a completion-source check and a
/// plain owner window — no MainViewModel, no network.
/// </summary>
public class ManualCheckSplashTests
{
    /// <summary>
    /// Advances the helper to completion: its awaits (the check, the patience, the display floor)
    /// resume on the headless dispatcher, which pumps only when told to.
    /// </summary>
    private static async Task<UpdateCheckResult?> PumpToCompletionAsync(Task<UpdateCheckResult?> run)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!run.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "the splash choreography never completed");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs(); // let the splash's own close jobs drain before asserts
        return await run;
    }

    [AvaloniaFact]
    public async Task PendingCheck_ShowsTheSplash_AndTheAnswerClosesIt()
    {
        var owner = new Window { Width = 300, Height = 200 };
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            TaskCompletionSource<UpdateCheckResult> check = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateCheckResult?> run = ManualCheckSplash.WaitAsync(
                owner, check.Task, patience: TimeSpan.FromSeconds(30), minimumDisplay: TimeSpan.Zero);
            Dispatcher.UIThread.RunJobs();

            SplashWindow splash = Assert.IsType<SplashWindow>(Assert.Single(owner.OwnedWindows));
            Assert.True(splash.IsVisible);
            // MODAL, not merely owned: the re-entrancy story (no second click while a wait is up)
            // rests entirely on modality, and OwnedWindows/IsVisible can't tell the two apart — a
            // Show(owner) mutant passed every other assert in this file.
            Assert.True(splash.IsDialog);
            Assert.False(splash.ShowInTaskbar); // the owner already has the app's taskbar entry

            check.SetResult(UpdateCheckResult.UpToDate);
            UpdateCheckResult? result = await PumpToCompletionAsync(run);

            Assert.Same(UpdateCheckResult.UpToDate, result); // the answer passes through untouched
            Assert.False(splash.IsVisible);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>
    /// An answer that arrives before the splash has even painted must not make it flicker for one
    /// frame: the splash stays up for the minimum display time, THEN the result comes back.
    /// </summary>
    [AvaloniaFact]
    public async Task FastAnswer_StillHoldsTheSplashForTheMinimumDisplay()
    {
        var owner = new Window { Width = 300, Height = 200 };
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            var stopwatch = Stopwatch.StartNew();
            Task<UpdateCheckResult?> run = ManualCheckSplash.WaitAsync(
                owner,
                Task.FromResult(UpdateCheckResult.UpToDate), // answered before the splash exists
                patience: TimeSpan.FromSeconds(30),
                minimumDisplay: TimeSpan.FromMilliseconds(250));
            Dispatcher.UIThread.RunJobs();

            SplashWindow splash = Assert.IsType<SplashWindow>(Assert.Single(owner.OwnedWindows));
            Assert.True(splash.IsVisible); // up despite the answer being ready

            UpdateCheckResult? result = await PumpToCompletionAsync(run);
            stopwatch.Stop();

            Assert.Same(UpdateCheckResult.UpToDate, result);
            Assert.False(splash.IsVisible);
            // Generous lower bound (floor 250ms) so timer granularity can't flake the assert.
            Assert.True(stopwatch.ElapsedMilliseconds >= 200,
                $"the splash was dismissed after {stopwatch.ElapsedMilliseconds}ms, before the display floor");
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>
    /// The user closing the splash DURING the display floor — after the answer already arrived —
    /// still suppresses the result. This is its own test because the guard is one easily-lost
    /// ternary on the floor path: a mutant that returned the result regardless passed every other
    /// test in this file, and the failure it allows is exactly the promise-breaker — the user
    /// dismisses the splash and a result dialog pops up anyway.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosingTheSplashDuringTheDisplayFloor_StillSuppressesTheResult()
    {
        var owner = new Window { Width = 300, Height = 200 };
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            // A generous floor: the close below must land inside it even on a stalled runner.
            Task<UpdateCheckResult?> run = ManualCheckSplash.WaitAsync(
                owner,
                Task.FromResult(UpdateCheckResult.UpToDate), // answered instantly — only the floor holds the splash
                patience: TimeSpan.FromSeconds(30),
                minimumDisplay: TimeSpan.FromSeconds(1));
            Dispatcher.UIThread.RunJobs();

            SplashWindow splash = Assert.IsType<SplashWindow>(Assert.Single(owner.OwnedWindows));
            splash.Close();
            Dispatcher.UIThread.RunJobs();

            UpdateCheckResult? result = await PumpToCompletionAsync(run);

            Assert.Null(result);
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>
    /// Closing the splash yourself means "stop waiting": no result dialog afterwards. The check
    /// itself cannot be cancelled (Velopack offers none) and keeps running elsewhere — what the
    /// user dismissed is the WAIT, so the helper answers null and the caller shows nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosingTheSplashYourself_SuppressesTheResult()
    {
        var owner = new Window { Width = 300, Height = 200 };
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            TaskCompletionSource<UpdateCheckResult> check = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateCheckResult?> run = ManualCheckSplash.WaitAsync(
                owner, check.Task, patience: TimeSpan.FromSeconds(30), minimumDisplay: TimeSpan.Zero);
            Dispatcher.UIThread.RunJobs();

            SplashWindow splash = Assert.IsType<SplashWindow>(Assert.Single(owner.OwnedWindows));
            splash.Close(); // the user's X / Alt+F4
            Dispatcher.UIThread.RunJobs();

            UpdateCheckResult? result = await PumpToCompletionAsync(run);

            Assert.Null(result);

            // The check answering afterwards changes nothing — there is nobody left waiting.
            check.SetResult(UpdateCheckResult.UpToDate);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>
    /// The patience expiring is answered exactly as the pre-splash flow answered it: a Failed
    /// result carrying the localized "still checking in the background" text — and the splash is
    /// gone, because a timeout report over a still-spinning splash would contradict itself.
    /// </summary>
    [AvaloniaFact]
    public async Task Timeout_ReportsTheCheckAsStillRunning_AndClosesTheSplash()
    {
        var owner = new Window { Width = 300, Height = 200 };
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            TaskCompletionSource<UpdateCheckResult> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateCheckResult?> run = ManualCheckSplash.WaitAsync(
                owner, never.Task, patience: TimeSpan.FromMilliseconds(100), minimumDisplay: TimeSpan.Zero);

            UpdateCheckResult? result = await PumpToCompletionAsync(run);

            Assert.NotNull(result);
            Assert.Equal(UpdateCheckStatus.Failed, result!.Status);
            Assert.Equal(Localizer.Instance["Main_CheckForUpdates_StillRunning"], result.FailureReason);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            owner.Close();
        }
    }
}
