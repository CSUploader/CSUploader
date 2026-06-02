// <copyright file="IInteractiveAuthService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;

namespace CSUploader.Services;

/// <summary>
/// Per-hoster configuration for an <see cref="IInteractiveAuthService"/> sign-in. Tells
/// the UI which URL to open in the embedded browser and which cookie to harvest once
/// the user has signed in.
/// </summary>
/// <param name="HosterName">Display name for the dialog header and for partitioning
/// per-hoster WebView2 user-data folders (so two hosters can't see each other's
/// cookies).</param>
/// <param name="LoginUrl">URL to navigate to on open (typically the hoster's
/// <c>/login.html</c>).</param>
/// <param name="CookieDomain">Cookie domain the named cookie is set against (e.g.
/// <c>.ex-load.com</c>). Currently informational — the WebView reads cookies via
/// origin, not domain — but kept on the spec so a future implementation can validate
/// the captured cookie's domain.</param>
/// <param name="CookieName">Name of the session cookie to capture (e.g. <c>xfss</c>).</param>
public readonly record struct InteractiveAuthSpec(
    string HosterName,
    string LoginUrl,
    string CookieDomain,
    string CookieName);

/// <summary>
/// Abstraction for prompting the user to complete an interactive sign-in (currently a
/// WebView2-hosted captcha login) and returning the resulting session cookie. Kept free
/// of WPF references so upload pipelines and tests can depend on it without dragging in
/// the UI assembly.
/// </summary>
public interface IInteractiveAuthService
{
    /// <summary>
    /// Prompts the user to sign in per <paramref name="spec"/> and returns the captured
    /// session cookie value, or null if the user cancelled or the sign-in was refused.
    /// </summary>
    /// <param name="spec">Per-hoster login parameters.</param>
    /// <param name="username">Account username being signed in. Currently informational —
    /// the WebView doesn't pre-fill or assert against it, but it's passed through so a
    /// future implementation can pre-populate the form or compare against the result.</param>
    /// <param name="proxy">Proxy the embedded browser should route through. Must match
    /// the proxy uploads will use for this account so the issuing IP matches the using
    /// IP (XFileSharing's session cookie is bound to the issuing IP and would otherwise
    /// be invalidated on the first request from a different IP). Pass
    /// <see cref="ProxyChoice.Direct"/> to sign in without a proxy. Pass <c>null</c> to
    /// signal "Use Proxies is enabled but no usable proxy is available" — the service
    /// will refuse to open the WebView and return null.</param>
    /// <param name="cancellationToken">Cancels the wait when the caller goes away. The
    /// WebView dialog itself stays open until the user closes it; cancellation just
    /// abandons the awaiter.</param>
    /// <returns>The captured cookie value on success, or null if the user cancelled,
    /// the proxy was refused (e.g. SOCKS-with-auth which WebView2 can't authenticate),
    /// or <paramref name="proxy"/> was null.</returns>
    /// <remarks>
    /// Implementations must marshal to the UI dispatcher themselves — callers may invoke
    /// this from a background upload thread. The WPF implementation also serialises
    /// concurrent calls so two simultaneous uploads on the same hoster don't pop two
    /// modal windows on top of each other.
    /// </remarks>
    Task<string?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken);
}
