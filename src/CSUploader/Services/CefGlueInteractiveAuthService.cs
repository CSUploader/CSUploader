// <copyright file="CefGlueInteractiveAuthService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if !WINDOWS
using System.Globalization;
using Avalonia.Controls;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.Views;     // WebViewLoginProxy, ProxyResolution
using CSUploader.Views.Cef; // CefGlueLoginWindow

namespace CSUploader.Services;

/// <summary>
/// Non-Windows (Linux/macOS) <see cref="IInteractiveAuthService"/> — the CefGlue analog of
/// <see cref="AvaloniaWebViewInteractiveAuthService"/>. Opens a modal <see cref="CefGlueLoginWindow"/> on the
/// UI thread to capture the session cookie / probe value, routing the embedded Chromium through the same proxy
/// uploads will use. It REUSES the WebView2 service's machinery verbatim — UI-thread marshaling via
/// <see cref="IUiDispatcher"/>, the <see cref="DialogOwnerResolver.ResolveVisibleMainOnly()"/> reveal-or-own
/// owner, the TCS bridge over <c>ShowDialog</c>, the queued-abort cancellation re-check and the
/// SOCKS-with-auth pre-window refusal — with ONE deliberate difference: a <b>process-wide</b> login gate.
/// </summary>
/// <remarks>
/// Concurrency (design §Concurrency, R1): the WebView2 head runs DIFFERENT hosters' logins in parallel behind
/// a per-hoster gate. That is only safe if two LIVE CEF browsers with distinct <see cref="Xilium.CefGlue.CefRequestContext"/>s
/// genuinely isolate — plausible but UNPROVEN. So the CEF head starts with a single static gate that
/// serializes ALL interactive logins; it relaxes to a per-hoster gate only after a real two-browser isolation
/// test passes. The gate is held until the dialog actually CLOSES (its release rides the dialog TCS), so a
/// caller whose token cancels abandons only its own await — the dialog stays up and the gate stays held, which
/// prevents a second window opening beside the orphan.
/// </remarks>
public sealed class CefGlueInteractiveAuthService(
    IDialogService dialogService,
    AppSettings settings,
    IUiDispatcher dispatcher,
    ITrayIconService trayIcon) : IInteractiveAuthService
{
    // PROCESS-WIDE: one gate for ALL logins on this head (not per-hoster). See the remarks above.
    private static readonly SemaphoreSlim LoginGate = new(1, 1);

    public async Task<InteractiveAuthResult?> AcquireSessionCookieAsync(
        InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
    {
        _ = username; // informational (parity with the WebView2 head — a future impl could pre-fill the form)

        // Null proxy = "Use Proxies is on but no usable proxy is available" — mirror the upload fail-fast.
        if (proxy is null)
        {
            return null;
        }

        await LoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // The gate is released EXACTLY when this login settles (dialog closed / refused / cancelled / errored),
        // i.e. when the TCS completes — NOT when a cancelled caller walks away. Wiring the release to the TCS up
        // front guarantees a single release on every path with no leak.
        TaskCompletionSource<InteractiveAuthResult?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = tcs.Task.ContinueWith(
            static (_, gate) => ((SemaphoreSlim)gate!).Release(),
            LoginGate,
            TaskScheduler.Default);

        try
        {
            // A cancel that landed while we queued behind the gate short-circuits before we marshal to the UI.
            cancellationToken.ThrowIfCancellationRequested();

            // Marshal onto the UI thread (typically called from an upload thread-pool thread). The action just
            // STARTS the async show and bridges it to the TCS; the try/catch makes the async-void body
            // escape-proof. IUiDispatcher.InvokeAsync takes no token, so re-check on the UI thread and mirror
            // the WebView2 head's queued-abort — complete the TCS and RETURN, never THROW here.
            await dispatcher.InvokeAsync(() =>
            {
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
        }
        catch (Exception ex)
        {
            // Faulting the TCS here settles this login → the release continuation runs → the gate is freed.
            tcs.TrySetException(ex);
            throw;
        }

        // Abandon ONLY the caller-facing await on cancellation; the dialog + gate are left untouched (the gate
        // frees when the dialog closes via the TCS continuation above).
        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<InteractiveAuthResult?> ShowLoginWindowAsync(InteractiveAuthSpec spec, ProxyChoice proxy)
    {
        ProxyResolution resolution = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        if (resolution.SocksAuthUnsupported)
        {
            // SOCKS-with-auth: Chromium can't carry SOCKS creds and there's no event to supply them (CEF has
            // the same limitation as WebView2). Be honest rather than silently using an unauthenticated hop.
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

        CefGlueLoginWindow window = new(
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
            allowInvalidCertificates: settings.AllowInvalidServerCertificates);

        return await window.ShowDialog<InteractiveAuthResult?>(owner);
    }

    // Reveal-or-own: identical to the WebView2 head. The owner is the VISIBLE MAIN window ONLY — deliberately
    // NOT the active-visible window — because this is a long-lived, BACKGROUND-triggered modal: parenting a
    // second login to the first active one would let Avalonia force-close the second mid-captcha when the first
    // completes. Null only under a non-desktop lifetime (headless) or a still-hidden main window.
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
#endif
