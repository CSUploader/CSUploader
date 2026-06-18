// <copyright file="IInteractiveAuthService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;

namespace CSUploader.Services;

/// <summary>
/// Per-hoster configuration for an <see cref="IInteractiveAuthService"/> sign-in. Tells
/// the UI which URL to open in the embedded browser, which cookie to harvest as the
/// session token, and (optionally) a second cookie carrying the user's identity.
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
/// <param name="UsernameCookieName">Optional name of a cookie carrying the signed-in
/// account's username/email — set by hosters that put the identity in the cookie jar
/// rather than only on the my_account page (ExtMatrix uses <c>username</c>). When non-
/// null and the cookie is present after sign-in, its value flows through as
/// <see cref="InteractiveAuthResult.CapturedUsername"/>. XFileSharing-family hosters
/// leave this null — their identity comes from the my_account scrape, not a cookie.</param>
/// <param name="CookieValueValidator">Optional predicate the WebView calls against the
/// captured cookie value before declaring sign-in success. When the cookie is set both
/// before AND after login (as with FileBoom's <c>accessToken</c> JWT, which is issued
/// with <c>aud:"client"</c> on the bootstrap call and re-issued with <c>aud:"user"</c>
/// after password validation), cookie-presence alone fires too early — we'd close the
/// window on the bootstrap token. The validator returns true only for the post-login
/// value (e.g. by JWT-decoding and inspecting an audience/role claim). Leave null for
/// hosters whose session cookie is set ONLY after login (XFileSharing family).</param>
/// <param name="AdditionalCookieNames">Optional list of supplementary cookies to capture
/// alongside <see cref="CookieName"/>. When non-empty and the named cookies are present
/// in the post-login jar, their values flow through as
/// <see cref="InteractiveAuthResult.AdditionalCookies"/>. Used by hosters whose
/// authenticated requests need more than one cookie (e.g. FileBoom sends both
/// <c>accessToken</c> and <c>pcId</c>).</param>
/// <param name="SuccessProbeScript">Optional JavaScript-probe sign-in signal for hosters whose
/// login sets no capturable login marker — e.g. HitFile, whose session cookies look identical
/// signed-in vs anonymous, so cookie-presence detection can't tell when login completed. When set,
/// the WebView runs this script on each poll tick; it must return a non-empty string once (and only
/// once) the user is authenticated, and that string flows back as
/// <see cref="InteractiveAuthResult.ProbeValue"/>. The script runs in the page's own context, so a
/// <c>fetch(..., {credentials:'include'})</c> it makes carries the full cookie jar (HttpOnly
/// included) automatically — letting the page fetch e.g. the account id directly, with no cookie
/// capture/forwarding on the C# side at all. Used instead of <see cref="CookieName"/>-based
/// completion; the cookie-based hosters leave it null.</param>
/// <param name="CookieCaptureUrl">Optional URL whose FULL cookie jar is captured (as a single
/// <c>name=value; name=value</c> header) into <see cref="InteractiveAuthResult.SessionCookieValue"/>
/// when a <see cref="SuccessProbeScript"/> sign-in completes. Lets a probe hoster ALSO hand the C#
/// side the logged-in cookies (HttpOnly included — captured via <c>CookieManager</c>, not
/// <c>document.cookie</c>) for later server-side calls the page can't make on demand — e.g.
/// HitFile's "Check / Refresh", which re-reads storage usage directly from C# through the proxy
/// using these cookies (<c>https://app.hitfile.net/</c>). Null for hosters that don't need the raw
/// jar (the probe value alone is their whole credential).</param>
public readonly record struct InteractiveAuthSpec(
    string HosterName,
    string LoginUrl,
    string CookieDomain,
    string CookieName,
    string? UsernameCookieName = null,
    Func<string, bool>? CookieValueValidator = null,
    IReadOnlyList<string>? AdditionalCookieNames = null,
    string? SuccessProbeScript = null,
    string? CookieCaptureUrl = null);

/// <summary>
/// Outcome of a successful <see cref="IInteractiveAuthService.AcquireSessionCookieAsync"/>
/// call. Bundles the session cookie value with any additional identity cookie the spec
/// asked the WebView to capture.
/// </summary>
/// <param name="SessionCookieValue">Value of the cookie named by
/// <see cref="InteractiveAuthSpec.CookieName"/>. Non-empty on a cookie-based success; empty for
/// <see cref="InteractiveAuthSpec.SuccessProbeScript"/> hosters (which return their credential via
/// <see cref="ProbeValue"/> instead).</param>
/// <param name="CapturedUsername">Value of the cookie named by
/// <see cref="InteractiveAuthSpec.UsernameCookieName"/>, or null when the spec didn't
/// request one or the cookie wasn't present. Hosters that use this as the canonical
/// identity should propagate it onto <c>AccountCheckResult.DerivedUsername</c> so the
/// EditAccount dialog and Accounts grid can surface it.</param>
/// <param name="AdditionalCookies">Name→value map of the cookies the spec asked for via
/// <see cref="InteractiveAuthSpec.AdditionalCookieNames"/>. Null when the spec didn't
/// request any. Missing names (cookie not in the post-login jar) are simply absent from
/// the map — callers handle them as optional.</param>
/// <param name="ProbeValue">The non-empty string returned by
/// <see cref="InteractiveAuthSpec.SuccessProbeScript"/> (e.g. HitFile's account id fetched by the
/// page itself). Null for cookie-based hosters.</param>
public readonly record struct InteractiveAuthResult(
    string SessionCookieValue,
    string? CapturedUsername,
    IReadOnlyDictionary<string, string>? AdditionalCookies = null,
    string? ProbeValue = null);

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
    /// session cookie value (plus any additional identity cookie the spec asked for), or
    /// null if the user cancelled or the sign-in was refused.
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
    /// <returns>The captured session + optional identity cookies on success, or null if
    /// the user cancelled, the proxy was refused (e.g. SOCKS-with-auth which WebView2
    /// can't authenticate), or <paramref name="proxy"/> was null.</returns>
    /// <remarks>
    /// Implementations must marshal to the UI dispatcher themselves — callers may invoke
    /// this from a background upload thread. The WPF implementation also serialises
    /// concurrent calls so two simultaneous uploads on the same hoster don't pop two
    /// modal windows on top of each other.
    /// </remarks>
    Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken);
}
