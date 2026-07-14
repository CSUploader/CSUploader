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
            // Marshal onto the UI thread (typically called from an upload thread-pool thread). The action just
            // STARTS the async show and bridges it to a TCS; the try/catch makes the async-void body escape-proof
            // (an unsunk async-void exception would otherwise crash the dispatcher loop). The gate is held for the
            // dialog's whole lifetime (await tcs.Task), preserving the "one dialog per hoster" invariant.
            TaskCompletionSource<InteractiveAuthResult?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.InvokeAsync(() =>
            {
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
            allowInvalidCertificates: settings.AllowInvalidServerCertificates);

        return await window.ShowDialog<InteractiveAuthResult?>(owner);
    }

    // Reveal-or-own (mirrors AvaloniaDialogService.GetOwnerOrRevealAsync): a modal demands a visible parent, so
    // a tray-hidden main window is revealed first. Null only under a non-desktop lifetime (headless).
    private Window? ResolveOwnerOrReveal()
    {
        Window? owner = DialogOwnerResolver.ResolveFromLifetime();
        if (owner is null)
        {
            trayIcon.ShowMainWindow();
            owner = DialogOwnerResolver.ResolveFromLifetime();
        }

        return owner;
    }
}
