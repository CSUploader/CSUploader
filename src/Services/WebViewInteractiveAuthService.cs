// <copyright file="WebViewInteractiveAuthService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// WPF implementation of <see cref="IInteractiveAuthService"/>. Opens a modal
/// <see cref="WebViewLoginWindow"/> on the UI dispatcher to capture the session cookie,
/// routing the embedded browser through the same proxy uploads will use so the issuing
/// IP matches the using IP (XFileSharing binds session cookies to the issuing IP).
/// Serialises concurrent calls per hoster so a burst of background uploads doesn't stack
/// N modal windows on top of each other.
/// </summary>
public sealed class WebViewInteractiveAuthService(IDialogService dialogService, AppSettings settings) : IInteractiveAuthService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHosterGates = new(StringComparer.OrdinalIgnoreCase);

    // Kept on the constructor so DI registration doesn't change shape if we re-introduce
    // an information dialog later (e.g. for SOCKS-with-auth notification). Currently used
    // only for the SOCKS-with-auth refusal message.
    private readonly IDialogService _dialogService = dialogService;

    // Read live each sign-in so the WebView honours the same "accept invalid server certificates"
    // opt-in uploads use — a MITM proxy or mis-issued hoster cert would otherwise block login only
    // (uploads, on the C# handler, already accept it).
    private readonly AppSettings _settings = settings;

    public async Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
    {
        // Null proxy = "Use Proxies is on but no usable proxy is available". The caller
        // (typically AccountVerifier or ExLoadPipeline) has already decided we shouldn't
        // sign in direct in that state. Mirror the upload fail-fast and return null.
        if (proxy is null)
        {
            return null;
        }

        // Per-hoster gate, not a global one — different hosters can interactively log in
        // in parallel (separate windows, separate user-data folders), but two uploads to
        // the same hoster must share a single login dialog. Once the first call wins,
        // the second call falls through and sees the cached cookie on its caller's side.
        SemaphoreSlim gate = _perHosterGates.GetOrAdd(spec.HosterName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Marshal to the WPF dispatcher — this method is typically called from an
            // upload pipeline running on a thread-pool thread, and Window construction
            // requires the UI thread. When no Application is up (unit tests that
            // accidentally hit this code path), there's nothing meaningful we can do,
            // so return null and let the caller surface a sensible "no session" failure.
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return null;
            }

            return await dispatcher.InvokeAsync(
                () => ShowLoginWindow(spec, proxy),
                DispatcherPriority.Normal,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private InteractiveAuthResult? ShowLoginWindow(InteractiveAuthSpec spec, ProxyChoice proxy)
    {
        ProxyCredentials? proxyCredentials = ResolveProxyCredentials(proxy, out string? refusalReason);
        if (refusalReason is not null)
        {
            // Unsupported proxy shape (currently: SOCKS with username/password — Chromium
            // exposes no way to satisfy the auth challenge). Tell the user clearly rather
            // than silently bypassing the auth (which would leak the request to the proxy
            // unauthenticated) and return null so the pipeline surfaces a failed sign-in.
            _dialogService.ShowError(refusalReason, Localizer.Instance["WebViewLogin_Error_UnsupportedProxy_Title"]);
            return null;
        }

        WebViewLoginWindow window = new(
            spec.HosterName,
            spec.LoginUrl,
            spec.CookieDomain,
            spec.CookieName,
            usernameCookieName: spec.UsernameCookieName,
            proxy: proxy,
            proxyCredentials: proxyCredentials,
            cookieValueValidator: spec.CookieValueValidator,
            additionalCookieNames: spec.AdditionalCookieNames,
            successProbeScript: spec.SuccessProbeScript,
            cookieCaptureUrl: spec.CookieCaptureUrl,
            userAgentOverride: spec.UserAgentOverride,
            allowInvalidCertificates: _settings.AllowInvalidServerCertificates)
        {
            Owner = Application.Current?.MainWindow,
        };

        window.ShowDialog();

        // Probe-script hosters (HitFile) return their credential via CapturedProbeValue; cookie
        // hosters return CapturedCookieValue. Either being set means a successful sign-in. A probe
        // hoster with CookieCaptureUrl set ALSO carries the captured cookie jar (as a Cookie header)
        // on SessionCookieValue, so the C# side can make logged-in calls later (HitFile refresh).
        if (window.CapturedProbeValue is { } probeValue)
        {
            return new InteractiveAuthResult(
                window.CapturedCookieValue ?? string.Empty,
                window.CapturedUsernameCookieValue,
                window.CapturedAdditionalCookies,
                probeValue);
        }

        if (window.CapturedCookieValue is { } cookieValue)
        {
            return new InteractiveAuthResult(
                cookieValue,
                window.CapturedUsernameCookieValue,
                window.CapturedAdditionalCookies);
        }

        return null;
    }

    /// <summary>
    /// Inspects the pinned proxy and returns either the credentials needed for WebView2's
    /// <see cref="Microsoft.Web.WebView2.Core.CoreWebView2.BasicAuthenticationRequested"/>
    /// handler, or a refusal reason when the proxy can't be supported (currently the
    /// only refusal case is SOCKS-with-auth — Chromium's <c>--proxy-server</c> flag
    /// can't carry SOCKS credentials and there's no event we can hook to supply them).
    /// </summary>
    private static ProxyCredentials? ResolveProxyCredentials(ProxyChoice proxy, out string? refusalReason)
    {
        refusalReason = null;

        if (proxy.Id == 0 || proxy.WebProxy is null)
        {
            // Direct connection — no credentials.
            return null;
        }

        // ProxyChoice doesn't carry the raw DTO, so we rely on Description to tell us the
        // scheme (built as "scheme://host:port" by ProxyManager.IProxySource.Next/GetById).
        bool isSocks = proxy.Description.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

        // Pull credentials from the IWebProxy. ProxyManager.BuildWebProxy sets these
        // when the ProxySettingDto has a username, otherwise leaves Credentials null.
        Uri probeUri = new("https://example.com/");
        System.Net.ICredentials? creds = proxy.WebProxy.Credentials;
        System.Net.NetworkCredential? networkCred = creds?.GetCredential(probeUri, "Basic");

        bool hasCredentials = !string.IsNullOrEmpty(networkCred?.UserName);
        if (!hasCredentials)
        {
            return null;
        }

        if (isSocks)
        {
            // WebView2 / Chromium has no public API to satisfy SOCKS auth in --proxy-server
            // mode. Be honest about it rather than silently using an unauthenticated SOCKS
            // hop the proxy server would reject.
            refusalReason = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["WebViewLogin_Error_SocksAuthUnsupported_Format"],
                proxy.Description);
            return null;
        }

        return new ProxyCredentials(networkCred!.UserName, networkCred.Password);
    }
}
