// <copyright file="AvaloniaWebViewInteractiveAuthServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// The headless-reachable branches of the Avalonia interactive-auth service (Phase 8 Task 6): the null-proxy
/// fail-fast, the SOCKS-with-auth refusal — both short-circuit BEFORE any window is created (so no live
/// WebView2 / desktop lifetime is needed) — plus the two Phase 8 gate fixes: the queued-marshal cancellation
/// parity (a cancel that lands while the marshal is queued throws WITHOUT opening a window) and the login-window
/// owner policy (background-triggered logins parent to the visible main window, never to an active sibling, so
/// one login surviving another's close). The real success path opens a native WebView and is the manual cutover
/// sign-in. Non-window tests use an inline IUiDispatcher (runs the marshal action synchronously).
/// </summary>
public class AvaloniaWebViewInteractiveAuthServiceTests
{
    private static InteractiveAuthSpec Spec() =>
        new("ex-load", "https://ex-load.com/login.html", ".ex-load.com", "xfss");

    [Fact]
    public async Task NullProxy_ReturnsNull_WithoutDispatchOrError()
    {
        var dialog = new Mock<IDialogService>(MockBehavior.Strict);
        var service = new AvaloniaWebViewInteractiveAuthService(
            dialog.Object, new AppSettings(), new InlineDispatcher(), Mock.Of<ITrayIconService>());

        InteractiveAuthResult? result = await service.AcquireSessionCookieAsync(
            Spec(), username: "u", proxy: null, CancellationToken.None);

        Assert.Null(result);
        dialog.VerifyNoOtherCalls(); // fail-fast: no error dialog on the null-proxy path
    }

    [Fact]
    public async Task SocksWithAuth_ShowsRefusal_ReturnsNull_WithoutWindow()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var socks = new ProxyChoice(9,
            new WebProxy("socks5://p:1080") { Credentials = new NetworkCredential("u", "pw") },
            "socks5://p:1080");
        var service = new AvaloniaWebViewInteractiveAuthService(
            dialog.Object, new AppSettings(), new InlineDispatcher(), Mock.Of<ITrayIconService>());

        InteractiveAuthResult? result = await service.AcquireSessionCookieAsync(
            Spec(), username: "u", proxy: socks, CancellationToken.None);

        Assert.Null(result);
        dialog.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CancelledWhileMarshalQueued_Throws_WithoutOpeningWindow()
    {
        // Phase 8 gate (cancellation parity): the WPF head passed the token into Dispatcher.InvokeAsync, so a
        // cancel landing while the marshal is queued aborted the invoke without opening the window. The port
        // re-checks the token on the UI thread inside the marshaled action. Strict mocks prove nothing was
        // touched: no window/dialog was opened and the owner was never resolved (no tray reveal).
        var dialog = new Mock<IDialogService>(MockBehavior.Strict);
        var tray = new Mock<ITrayIconService>(MockBehavior.Strict);
        using var cts = new CancellationTokenSource();
        var service = new AvaloniaWebViewInteractiveAuthService(
            dialog.Object, new AppSettings(), new CancelThenRunDispatcher(cts), tray.Object);

        // The dispatcher double cancels the token THEN runs the marshaled action (the cancel "lands while the
        // marshal is queued"); the on-UI-thread re-check must TrySetCanceled so the await throws.
        await Assert.ThrowsAsync<TaskCanceledException>(() => service.AcquireSessionCookieAsync(
            Spec(), username: "u", proxy: ProxyChoice.Direct, cts.Token));

        dialog.VerifyNoOtherCalls(); // no error dialog / no window created
        tray.VerifyNoOtherCalls();   // owner never resolved, so no tray reveal
    }

    [AvaloniaFact]
    public async Task LoginWindowsOwnedByMain_SurviveSiblingClose_WhereSiblingOwnedWouldDie()
    {
        // Phase 8 gate (owner policy, MAJOR): a background-triggered login must be owned by the VISIBLE MAIN
        // window (ResolveVisibleMainOnly), never by another active login (ResolveFromLifetime's active-first
        // pick). A live WebView2 login is manual-only, so the CONSEQUENCE that makes the policy load-bearing is
        // exercised with plain windows completing via ShowDialog<string?> (Close(result) — the same completion
        // contract WebViewLoginWindow uses). Two halves in one test tie survival to the owner CHOICE:
        //   (1) sibling-owned (the OLD active-first pick for a second login) → force-closed when the sibling
        //       closes, ShowDialog silently returns null; and
        //   (2) main-owned (the FIX) → survives the sibling's close and delivers its OWN result.
        var main = new Window { Width = 200, Height = 200 };
        try
        {
            main.Show();
            Dispatcher.UIThread.RunJobs();

            // (1) B parented to the already-open, active login A — what active-visible-first would produce for a
            //     second login. Closing A force-closes B (Avalonia's owned-window cascade); B never delivers a result.
            var a = new Window { Width = 100, Height = 100 };
            var b = new Window { Width = 100, Height = 100 };
            try
            {
                Task<string?> ta = a.ShowDialog<string?>(main);
                Dispatcher.UIThread.RunJobs();
                Task<string?> tb = b.ShowDialog<string?>(a);
                Dispatcher.UIThread.RunJobs();

                a.Close("A");
                Dispatcher.UIThread.RunJobs();

                Assert.False(b.IsVisible);   // synchronous guard: A's close force-closed B (never awaits a live dialog)
                Assert.Equal("A", await ta);
                Assert.Null(await tb);        // B returned null, not its own result — the bug the fix prevents
            }
            finally
            {
                b.Close();
                a.Close();
            }

            // (2) C and D BOTH owned by main (the fixed ResolveVisibleMainOnly pick). Closing C does NOT force-close
            //     D, and D delivers its own result — the verifier's prescribed survival case.
            var c = new Window { Width = 100, Height = 100 };
            var d = new Window { Width = 100, Height = 100 };
            try
            {
                Task<string?> tc = c.ShowDialog<string?>(main);
                Dispatcher.UIThread.RunJobs();
                Task<string?> td = d.ShowDialog<string?>(main);
                Dispatcher.UIThread.RunJobs();

                c.Close("C");
                Dispatcher.UIThread.RunJobs();

                Assert.True(d.IsVisible);    // D survived its sibling's close
                Assert.Equal("C", await tc);

                d.Close("D");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("D", await td); // D delivered its OWN result, not null
            }
            finally
            {
                d.Close();
                c.Close();
            }
        }
        finally
        {
            main.Close();
        }
    }

    // Inline IUiDispatcher: runs the marshal action synchronously so the SOCKS refusal (which never opens a
    // window) resolves without a real UI thread. Timer/Post are unused on these paths.
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => throw new NotSupportedException();
    }

    // Models the WPF queued-abort: cancels the token, THEN runs the marshaled action synchronously — i.e. the
    // cancel lands while the marshal is still "queued", so the service's on-UI-thread re-check trips.
    private sealed class CancelThenRunDispatcher(CancellationTokenSource cts) : IUiDispatcher
    {
        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            cts.Cancel();
            action();
            return Task.CompletedTask;
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => throw new NotSupportedException();
    }
}
