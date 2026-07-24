// <copyright file="AvaloniaWebViewInteractiveAuthService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Controls;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Avalonia <see cref="IInteractiveAuthService"/> (port of the WPF WebViewInteractiveAuthService). Opens a
/// modal <see cref="WebViewLoginWindow"/> on the UI thread to capture the session cookie / probe value,
/// routing the embedded browser through the same proxy uploads will use (XFS binds session cookies to the
/// issuing IP). Serialises concurrent calls per hoster so a burst of background uploads doesn't stack N
/// modal windows. Sheds the WPF dispatcher for <see cref="IUiDispatcher"/> (design line 79); resolves the
/// owner (reveal-or-own) via <see cref="DialogOwnerResolver"/> since Avalonia ShowDialog rejects a null /
/// hidden owner and this app hides its main window to the tray.
/// </summary>
public sealed class AvaloniaWebViewInteractiveAuthService(
    IDialogService dialogService,
    AppSettings settings,
    IUiDispatcher dispatcher,
    ITrayIconService trayIcon) : IInteractiveAuthService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHosterGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<InteractiveAuthResult?> AcquireSessionCookieAsync(
        InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
    {
        _ = username; // informational (parity with WPF — a future impl could pre-fill the form)

        // Null proxy = "Use Proxies is on but no usable proxy is available" — mirror the upload fail-fast.
        if (proxy is null)
        {
            return null;
        }

        // Per-hoster gate: different hosters sign in in parallel (separate windows / user-data folders), but
        // two uploads to the SAME hoster share one dialog.
        SemaphoreSlim gate = _perHosterGates.GetOrAdd(spec.HosterName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A cancel that landed while we queued behind the gate short-circuits before we ever marshal to the
            // UI thread. Safe here: we're on a thread-pool thread, inside the try, so the finally still releases
            // the gate (the WPF head got this for free — its token rode Dispatcher.InvokeAsync).
            cancellationToken.ThrowIfCancellationRequested();

            // Marshal onto the UI thread (typically called from an upload thread-pool thread). The action just
            // STARTS the async show and bridges it to a TCS; the try/catch makes the async-void body escape-proof
            // (an unsunk async-void exception would otherwise crash the dispatcher loop). The gate is held for the
            // dialog's whole lifetime (await tcs.Task), preserving the "one dialog per hoster" invariant.
            TaskCompletionSource<InteractiveAuthResult?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.InvokeAsync(() =>
            {
                // WPF parity: the WPF head passes the token into Dispatcher.InvokeAsync (WebViewInteractiveAuthService.cs:71-74),
                // so a cancel that lands while the marshal is still QUEUED aborts the invoke WITHOUT opening the
                // window. Avalonia's IUiDispatcher.InvokeAsync takes no token, so re-check on the UI thread and
                // mirror that queued-abort. Complete the TCS and RETURN — never THROW here: a throw inside the
                // marshaled action escapes to the framework's unhandled-exception path (IUiDispatcher.InvokeAsync
                // contract) leaving the TCS unset, which would hang this call and the per-hoster gate forever.
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                async void Pump()
                {
                    try
                    {
                        tcs.TrySetResult(await ShowLoginWindowAsync(spec, proxy));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }

                Pump();
            }).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<InteractiveAuthResult?> ShowLoginWindowAsync(InteractiveAuthSpec spec, ProxyChoice proxy)
    {
        ProxyResolution resolution = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        if (resolution.SocksAuthUnsupported)
        {
            // SOCKS-with-auth: Chromium can't carry SOCKS creds and there's no event to supply them. Be honest
            // rather than silently using an unauthenticated SOCKS hop, and return null so the pipeline surfaces
            // a failed sign-in.
            await dialogService.ShowErrorAsync(
                string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Error_SocksAuthUnsupported_Format"], proxy.Description),
                Localizer.Instance["WebViewLogin_Error_UnsupportedProxy_Title"]);
            return null;
        }

        Window? owner = ResolveOwnerOrReveal();
        if (owner is null)
        {
            return null; // no window available to own the modal (headless / no lifetime)
        }

        WebViewLoginWindow window = new(
            spec.HosterName,
            spec.LoginUrl,
            spec.CookieDomain,
            spec.CookieName,
            usernameCookieName: spec.UsernameCookieName,
            proxy: proxy,
            proxyCredentials: resolution.Credentials,
            cookieValueValidator: spec.CookieValueValidator,
            additionalCookieNames: spec.AdditionalCookieNames,
            successProbeScript: spec.SuccessProbeScript,
            cookieCaptureUrl: spec.CookieCaptureUrl,
            userAgentOverride: spec.UserAgentOverride,
            allowInvalidCertificates: settings.AllowInvalidServerCertificates,
            captureOnlyAfterLeavingLoginPage: spec.CaptureOnlyAfterLeavingLoginPage);

        return await window.ShowDialog<InteractiveAuthResult?>(owner);
    }

    // Reveal-or-own: a modal login demands a visible parent, so a tray-hidden main window is revealed first.
    // The owner is the VISIBLE MAIN window ONLY (ResolveVisibleMainOnly), deliberately NOT the active-visible
    // window AvaloniaDialogService.GetOwnerOrRevealAsync picks (ResolveFromLifetime). This service is a
    // long-lived, BACKGROUND-triggered modal: a burst of uploads can open one hoster's login while another
    // hoster's login is already up and active. Avalonia force-closes an owned window when its owner closes, so
    // parenting a second login to the first active one (what active-visible-first would do) would kill the
    // second mid-captcha the instant the first completes — exactly the hazard DialogOwnerResolver.cs:59-63
    // warns a long-lived surface must never take. WPF parented every login to MainWindow
    // (WebViewInteractiveAuthService.cs:112 Owner = MainWindow); main-only preserves that. Null only under a
    // non-desktop lifetime (headless) or a still-hidden main window.
    private Window? ResolveOwnerOrReveal()
    {
        Window? owner = DialogOwnerResolver.ResolveVisibleMainOnly();
        if (owner is null)
        {
            trayIcon.ShowMainWindow();
            owner = DialogOwnerResolver.ResolveVisibleMainOnly();
        }

        return owner;
    }
}
