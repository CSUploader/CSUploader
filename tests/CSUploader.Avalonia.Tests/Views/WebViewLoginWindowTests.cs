// <copyright file="WebViewLoginWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Headless.XUnit;
using CSUploader.Lib.Net;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless construction of the Avalonia WebView login window (Phase 8 Task 3). Constructing WITHOUT showing
/// never attaches the NativeControlHost, so no child HWND / WebView2 is created — safe headlessly. The live
/// controller + real sign-in are the maintainer's manual cutover step (design line 88; agent can't grab foreground).
/// </summary>
public class WebViewLoginWindowTests
{
    [AvaloniaFact]
    public void Constructs_WithVmDataContext_AndFormattedHeader()
    {
        var window = new WebViewLoginWindow("ex-load", "about:blank", ".ex-load.com", "xfss",
            proxy: ProxyChoice.Direct);
        try
        {
            var vm = Assert.IsType<WebViewLoginViewModel>(window.DataContext);
            Assert.Contains("ex-load", vm.Header, StringComparison.Ordinal); // WebViewLogin_Header_Format applied
            Assert.False(vm.IsInitialized);         // no HwndReady until shown
            Assert.False(vm.IsCompleted);
            Assert.Equal(0, vm.NavigationCompletedCount);
        }
        finally
        {
            // The existing window tests Show() then Close() in a finally (e.g. UploadWizardSummaryTests.cs:44,77;
            // SettingsAccountsTests.cs:36,64). This test deliberately NEVER shows the window — showing attaches
            // the NativeControlHost -> HwndReady -> a real CoreWebView2 (native, Evergreen-runtime resources),
            // which must not happen headlessly. A never-shown window created no platform peer, so there is
            // nothing to close; the guard is a no-op here and documents that divergence.
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }
}
