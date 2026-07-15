// <copyright file="UpdateProgressSinkTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Services;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// Headless tests for the real <see cref="AvaloniaUpdateProgressSink"/> (Phase 4 Task 8). Under the
/// headless lifetime <see cref="DialogOwnerResolver.ResolveFromLifetime"/> returns <c>null</c> (not a
/// desktop lifetime), so <see cref="AvaloniaUpdateProgressSink.Open"/> takes the ownerless
/// <c>Show()</c> branch — which is exactly the tray-hidden / no-owner path. The load-bearing contracts:
/// each <c>Open</c> creates a FRESH window; <c>Report</c>/<c>SetStatus</c> drive the most-recent one;
/// the sink NEVER programmatically closes the window (WPF parity — on success the process restarts, on
/// failure it stays up showing the error); and <c>Report</c> before <c>Open</c> is a null-window no-op.
/// A separate test pins the reason the sink resolves a visible owner: Avalonia's <c>Show(owner)</c> throws
/// on a hidden owner (§Reality-check #12). Every shown window is closed in a <c>finally</c> (headless
/// windows are process-global for the session).
/// </summary>
public class UpdateProgressSinkTests
{
    [AvaloniaFact]
    public void Open_UnderHeadless_ShowsOwnerlessWindow()
    {
        var sink = new AvaloniaUpdateProgressSink();
        try
        {
            sink.Open();
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(sink.CurrentWindow);
            Assert.True(sink.CurrentWindow!.IsVisible);
        }
        finally
        {
            sink.Close();
        }
    }

    [AvaloniaFact]
    public void Open_CreatesFreshWindowEachCall()
    {
        var sink = new AvaloniaUpdateProgressSink();
        UpdateProgressWindow? first = null;
        UpdateProgressWindow? second = null;
        try
        {
            sink.Open();
            Dispatcher.UIThread.RunJobs();
            first = sink.CurrentWindow;

            sink.Open();
            Dispatcher.UIThread.RunJobs();
            second = sink.CurrentWindow;

            Assert.NotNull(first);
            Assert.NotNull(second);

            // The interface's retry contract: a second Open replaces the surface with a fresh window, and
            // Report/SetStatus then target the most-recent one. The previous window is NOT closed (WPF
            // parity), so both are closed explicitly below.
            Assert.NotSame(first, second);
        }
        finally
        {
            first?.Close();
            second?.Close();
        }
    }

    [AvaloniaFact]
    public void Report_UpdatesProgressBarAndPercent()
    {
        var sink = new AvaloniaUpdateProgressSink();
        try
        {
            sink.Open();
            sink.Report(42);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(42d, sink.CurrentWindow!.Progress.Value);
            Assert.Equal("42%", sink.CurrentWindow.PercentText.Text);
        }
        finally
        {
            sink.Close();
        }
    }

    [AvaloniaFact]
    public void Lifecycle_DrivesStatusAndBar_AndNeverAutoCloses()
    {
        var sink = new AvaloniaUpdateProgressSink();
        try
        {
            // Mirror MainViewModel.InstallUpdateAsync: Open, downloading status, the Progress<int> pump,
            // then the restarting status. The sink must show the window and forward every call WITHOUT
            // ever closing it — the never-Close contract (the caller only ever Opens).
            sink.Open();
            sink.SetStatus("Downloading update v1.2.3…");
            sink.Report(40);
            sink.Report(80);
            sink.Report(100);
            sink.SetStatus("Restarting to apply update…");
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(sink.CurrentWindow);
            Assert.True(sink.CurrentWindow!.IsVisible); // never auto-closed across the whole lifecycle
            Assert.Equal("Restarting to apply update…", sink.CurrentWindow.StatusText.Text);
            Assert.Equal(100d, sink.CurrentWindow.Progress.Value);
            Assert.Equal("100%", sink.CurrentWindow.PercentText.Text);
        }
        finally
        {
            sink.Close();
        }
    }

    [AvaloniaFact]
    public void ReportAndSetStatus_BeforeOpen_AreNoOps()
    {
        var sink = new AvaloniaUpdateProgressSink();

        // No window yet → the null-conditional forwards are no-ops, not NREs (WPF parity: the WPF sink's
        // _window?.SetProgress). Nothing to close.
        sink.Report(50);
        sink.SetStatus("ignored");

        Assert.Null(sink.CurrentWindow);
    }

    [AvaloniaFact]
    public void Close_ClosesWindowAndClearsReference()
    {
        var sink = new AvaloniaUpdateProgressSink();
        UpdateProgressWindow? window = null;

        sink.Open();
        Dispatcher.UIThread.RunJobs();
        window = sink.CurrentWindow;
        Assert.NotNull(window);
        Assert.True(window!.IsVisible);

        sink.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(sink.CurrentWindow);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void ShowWithHiddenOwner_Throws_WhichIsWhyTheSinkResolvesAVisibleOwner()
    {
        var owner = new Window { Width = 100, Height = 100 };
        UpdateProgressWindow? child = null;
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            owner.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.False(owner.IsVisible);

            child = new UpdateProgressWindow();

            // §Reality-check #12: Show(owner) throws on a hidden owner exactly like ShowDialog does. This is
            // the whole reason AvaloniaUpdateProgressSink.Open resolves a VISIBLE owner (the resolver never
            // returns a hidden window) and shows ownerless when the resolver returns null — it must never
            // pass a hidden owner to Show(owner).
            Exception? ex = Record.Exception(() => child.Show(owner));
            Assert.IsType<InvalidOperationException>(ex);
        }
        finally
        {
            if (child is { IsVisible: true })
            {
                child.Close();
            }

            owner.Close();
        }
    }
}
