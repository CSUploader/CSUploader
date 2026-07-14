// <copyright file="AvaloniaWebViewInteractiveAuthServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// The two headless-reachable branches of the Avalonia interactive-auth service (Phase 8 Task 6): the
/// null-proxy fail-fast and the SOCKS-with-auth refusal — both short-circuit BEFORE any window is created
/// (so no live WebView2 / desktop lifetime is needed). The success path opens a native WebView and is the maintainer's
/// cutover sign-in. Uses an inline IUiDispatcher (runs the marshal action synchronously).
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
}
