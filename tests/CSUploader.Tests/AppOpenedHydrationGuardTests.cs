// <copyright file="AppOpenedHydrationGuardTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Pins the one-shot hydration guard on the Avalonia head's <c>mainWindow.Opened</c> wiring
/// (<c>App.axaml.cs</c>). WPF's <c>Loaded</c> fires once, but Avalonia re-raises <c>Opened</c> on every
/// <c>Hide()</c>-&gt;<c>Show()</c>, which Phase 7 close-to-tray makes reachable — so the head guards the
/// hydration body (the non-idempotent <c>MainViewModel.InitializeAsync</c>) to run exactly once. This test
/// mirrors that wiring SHAPE with a counting guarded <c>Opened</c> handler and proves both facts together:
/// <c>Opened</c> re-fires across Show/Hide/Show, yet the guarded body runs only once.
/// </summary>
public class AppOpenedHydrationGuardTests
{
    [AvaloniaFact]
    public void OpenedHydration_RunsOnce_EvenThoughOpenedRefiresOnHideThenShow()
    {
        int openedFires = 0;
        int bodyRuns = 0;
        bool hydrated = false; // mirrors App.axaml.cs: captured before the Opened subscription

        var w = new Window();
        w.Opened += (_, _) =>
        {
            openedFires++;
            if (hydrated)
            {
                return;
            }

            hydrated = true;
            bodyRuns++;
        };

        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            w.Hide();
            Dispatcher.UIThread.RunJobs();
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(openedFires > 1, $"expected Opened to re-fire on Hide()->Show(), saw {openedFires}");
            Assert.Equal(1, bodyRuns); // the one-shot guard held despite the Opened re-fire
        }
        finally
        {
            w.Close();
        }
    }
}
