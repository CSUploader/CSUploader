// <copyright file="CefLoginHandlers.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if !WINDOWS
using Xilium.CefGlue;
using CommonHandlers = Xilium.CefGlue.Common.Handlers;

namespace CSUploader.Views.Cef;

/// <summary>
/// Lifespan handler for <see cref="CefGlueLoginWindow"/>. Surfaces the CEF <c>OnAfterCreated</c> /
/// <c>OnBeforeClose</c> callbacks (both fire on the CEF UI thread) as events the window uses to (a) capture the
/// native <see cref="CefBrowser"/> for DevTools/close, and (b) drive the async teardown wait — the window's
/// <c>CloseBrowser(true)</c> is only truly done once <c>OnBeforeClose</c> has fired (design §Async teardown).
/// </summary>
internal sealed class CefLoginLifeSpanHandler : CommonHandlers.LifeSpanHandler
{
    /// <summary>Raised on the CEF UI thread once the browser exists (design init-in-flight race guard consumes
    /// this to close a browser that was created after teardown).</summary>
    public event Action<CefBrowser>? AfterCreated;

    /// <summary>Raised on the CEF UI thread once the browser has fully closed — the teardown completion signal.</summary>
    public event Action? BeforeClose;

    protected override void OnAfterCreated(CefBrowser browser) => AfterCreated?.Invoke(browser);

    protected override void OnBeforeClose(CefBrowser browser) => BeforeClose?.Invoke();
}

/// <summary>
/// Browser-level request handler for <see cref="CefGlueLoginWindow"/>: accepts invalid certificates when the
/// user opted in (<c>OnCertificateError → Continue</c>, the CEF analog of WebView2's <c>AlwaysAllow</c>), feeds
/// HTTP/HTTPS proxy Basic credentials on the 407 challenge (<c>GetAuthCredentials(isProxy:true)</c>), and — only
/// when a User-Agent override is configured — rewrites the <c>User-Agent</c> header on every subresource via a
/// per-request <see cref="CefLoginResourceRequestHandler"/> (paired with the window's DevTools
/// <c>Network.setUserAgentOverride</c>, since a header rewrite alone does not change <c>navigator.userAgent</c>).
/// </summary>
internal sealed class CefLoginRequestHandler(
    bool allowInvalidCertificates,
    ProxyCredentials? proxyCredentials,
    string? userAgentOverride) : CommonHandlers.RequestHandler
{
    protected override bool OnCertificateError(
        CefBrowser browser, CefErrorCode certError, string requestUrl, CefSslInfo sslInfo, CefCallback callback)
    {
        // Only ever reached when the user explicitly enabled AllowInvalidServerCertificates; Continue() ==
        // the C# handler's DangerousAcceptAnyServerCertificateValidator. Otherwise leave it to CEF's default
        // (return false → the error stands).
        if (allowInvalidCertificates)
        {
            callback.Continue();
            return true;
        }

        callback.Cancel();
        return false;
    }

    protected override bool GetAuthCredentials(
        CefBrowser browser, string originUrl, bool isProxy, string host, int port, string realm, string scheme, CefAuthCallback callback)
    {
        // Only the proxy (407) challenge is ours to answer; an origin 401 is the user's own login and must be
        // left to the page. Mirrors the WebView2 head feeding proxy creds (there it also answered 401 harmlessly;
        // here CEF distinguishes isProxy, so we answer only the proxy case).
        if (isProxy && proxyCredentials is not null)
        {
            callback.Continue(proxyCredentials.Username, proxyCredentials.Password ?? string.Empty);
            return true;
        }

        callback.Cancel();
        return false;
    }

    protected override CefResourceRequestHandler? GetResourceRequestHandler(
        CefBrowser browser, CefFrame frame, CefRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => string.IsNullOrEmpty(userAgentOverride) ? null : new CefLoginResourceRequestHandler(userAgentOverride);
}

/// <summary>
/// Per-request resource handler that rewrites the <c>User-Agent</c> header so subresources present the
/// spec's override UA (cf_clearance binds to the exact solving UA). Only instantiated when a UA override is set.
/// </summary>
internal sealed class CefLoginResourceRequestHandler(string userAgent) : CefResourceRequestHandler
{
    protected override CefCookieAccessFilter? GetCookieAccessFilter(CefBrowser browser, CefFrame frame, CefRequest request)
        => null; // no cookie filtering — the login context's jar is read directly via VisitUrlCookies

    protected override CefReturnValue OnBeforeResourceLoad(CefBrowser browser, CefFrame frame, CefRequest request, CefCallback callback)
    {
        request.SetHeaderByName("User-Agent", userAgent, overwrite: true);
        return CefReturnValue.Continue;
    }
}

/// <summary>
/// Per-login request-context handler. Sets the Chromium proxy preference ON THIS CONTEXT once it is
/// initialized (design: after context init, on the CEF UI thread — <c>OnRequestContextInitialized</c> fires
/// there), so the embedded browser routes through the same proxy uploads will use. Returns no resource handler
/// (the browser-level <see cref="CefLoginRequestHandler"/> owns UA rewriting).
/// </summary>
internal sealed class CefLoginRequestContextHandler(string? proxyServer) : CefRequestContextHandler
{
    protected override CefResourceRequestHandler? GetResourceRequestHandler(
        CefBrowser browser, CefFrame frame, CefRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => null;

    protected override void OnRequestContextInitialized(CefRequestContext requestContext)
    {
        if (string.IsNullOrEmpty(proxyServer))
        {
            return;
        }

        // Chromium proxy config: { mode: "fixed_servers", server: "scheme://host:port" }. proxyServer is
        // already scheme://host:port (WebViewLoginProxy.BuildProxyServerArg → ProxyChoice.Description).
        using CefDictionaryValue dict = CefDictionaryValue.Create();
        dict.SetString("mode", "fixed_servers");
        dict.SetString("server", proxyServer);

        using CefValue value = CefValue.Create();
        value.SetDictionary(dict);

        requestContext.SetPreference("proxy", value, out _);
    }
}

/// <summary>
/// Cookie visitor that collects a URL's cookies (HttpOnly included) off the CEF IO thread and completes the
/// supplied TCS once the last cookie has been visited. The awaiting caller (on the UI thread) reads the result
/// and marshals it once — the IO-thread → <c>TaskCompletionSource(RunContinuationsAsynchronously)</c> → UI
/// contract (design §Thread affinity). A URL with no cookies never triggers <c>Visit</c>, so the window arms a
/// zero-result timeout around this.
/// </summary>
internal sealed class CefLoginCookieCollector(TaskCompletionSource<IReadOnlyList<CefCookie>> tcs) : CefCookieVisitor
{
    private readonly List<CefCookie> _cookies = [];

    protected override bool Visit(CefCookie cookie, int count, int total, out bool delete)
    {
        delete = false;
        _cookies.Add(cookie);
        if (count + 1 >= total)
        {
            tcs.TrySetResult(_cookies.ToArray());
        }

        return true; // keep visiting the remaining cookies
    }
}

/// <summary>
/// Delete-cookies callback that completes the supplied TCS once CEF's async <c>DeleteCookies</c> has finished
/// (fires on the CEF IO thread). Lets the window AWAIT the stale-cookie wipe before navigating (design R1).
/// </summary>
internal sealed class CefLoginDeleteCookiesCallback(TaskCompletionSource tcs) : CefDeleteCookiesCallback
{
    protected override void OnComplete(int numDeleted) => tcs.TrySetResult();
}
#endif
