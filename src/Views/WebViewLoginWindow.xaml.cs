// <copyright file="WebViewLoginWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using Microsoft.Web.WebView2.Core;

namespace CSUploader.Views;

/// <summary>
/// Modal browser window used to capture a session cookie from a hoster whose login is
/// gated behind a captcha (currently ex-load.com / hCaptcha). Hosts a WebView2 navigated
/// to <see cref="_loginUrl"/>; once the named cookie appears in the cookie store the
/// window closes with <see cref="CapturedCookieValue"/> populated.
/// </summary>
/// <remarks>
/// <para>
/// WebView2 stores user-data (cookies, cache, hCaptcha challenge state) per user-data-folder.
/// We point each instance at a folder under <c>%LocalAppData%\CSUploader\WebView2\&lt;hoster&gt;</c>
/// so a user re-opening the window doesn't have to re-solve the captcha after a recent
/// session — the prior cookies and hCaptcha trust state are still there. The folder is
/// per-hoster so two different hosters can't leak cookies into each other's stores.
/// </para>
/// <para>
/// Detection logic: after every <see cref="CoreWebView2.NavigationCompleted"/>, we read
/// the cookies for the login origin and close the window when the named cookie appears
/// with a non-empty value. Across the XFileSharing family the session cookie (typically
/// <c>xfss</c>) is only set after credentials validate — the login page itself does NOT
/// set it. Hxfile in particular redirects post-login back to <c>/login.html</c> (with
/// the cookie set), so the URL alone can't be used as the "logged in" signal — the
/// cookie is.
/// </para>
/// <para>
/// Proxy routing: when <see cref="_proxy"/> is non-null and not direct, the embedded
/// browser is initialised with <c>--proxy-server=scheme://host:port</c> so its traffic
/// goes through the same proxy the upload pipeline uses for this account. XFileSharing
/// binds session cookies to the issuing IP, so the sign-in MUST share the upload's IP or
/// the cookie would be invalidated on the first real upload. HTTP/HTTPS proxy
/// authentication is satisfied via <see cref="CoreWebView2.BasicAuthenticationRequested"/>.
/// </para>
/// </remarks>
public partial class WebViewLoginWindow : Window
{
    private readonly string _hosterName;
    private readonly string _loginUrl;
    private readonly string _cookieDomain;
    private readonly string _cookieName;
    private readonly string? _usernameCookieName;
    private readonly Func<string, bool>? _cookieValueValidator;
    private readonly IReadOnlyList<string>? _additionalCookieNames;
    private readonly ProxyChoice? _proxy;
    private readonly ProxyCredentials? _proxyCredentials;
    private bool _initialized;
    private bool _completed;
    private DispatcherTimer? _pollTimer;

    /// <summary>How often the cookie poll fires. The XFileSharing-family hosters complete
    /// sign-in via a full POST→302 round-trip so <see cref="CoreWebView2.NavigationCompleted"/>
    /// already catches the cookie within a couple of seconds — the poll is just a safety
    /// net there. FileBoom and other SPA-shaped hosters log in via XHR + history.pushState
    /// (no NavigationCompleted fires), so the poll is the ONLY signal we get. 1 s strikes a
    /// reasonable balance between perceived UI latency and cookie-store read pressure.</summary>
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1);

    public WebViewLoginWindow(
        string hosterName,
        string loginUrl,
        string cookieDomain,
        string cookieName,
        string? usernameCookieName = null,
        ProxyChoice? proxy = null,
        ProxyCredentials? proxyCredentials = null,
        Func<string, bool>? cookieValueValidator = null,
        IReadOnlyList<string>? additionalCookieNames = null)
    {
        _hosterName = hosterName;
        _loginUrl = loginUrl;
        _cookieDomain = cookieDomain;
        _cookieName = cookieName;
        _usernameCookieName = usernameCookieName;
        _cookieValueValidator = cookieValueValidator;
        _additionalCookieNames = additionalCookieNames;
        _proxy = proxy;
        _proxyCredentials = proxyCredentials;

        InitializeComponent();

        HeaderText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["WebViewLogin_Header_Format"],
            hosterName);
        StatusText.Text = Localizer.Instance["WebViewLogin_Status_Initializing"];

        Loaded += WebViewLoginWindow_Loaded;
        Closed += WebViewLoginWindow_Closed;
    }

    /// <summary>
    /// Value of the captured session cookie, or null when the user cancelled before login
    /// completed. Set immediately before <see cref="Window.DialogResult"/> flips to true.
    /// </summary>
    public string? CapturedCookieValue { get; private set; }

    /// <summary>
    /// Value of the captured identity cookie (the one named by the optional
    /// <c>usernameCookieName</c> ctor parameter), or null when the spec didn't request
    /// one or the cookie wasn't present in the WebView2 cookie jar. Set in the same
    /// NavigationCompleted pass as <see cref="CapturedCookieValue"/>.
    /// </summary>
    public string? CapturedUsernameCookieValue { get; private set; }

    /// <summary>
    /// Name→value map of the supplementary cookies named by <c>additionalCookieNames</c>
    /// (e.g. FileBoom's <c>pcId</c>), populated alongside <see cref="CapturedCookieValue"/>
    /// during the NavigationCompleted that closes the window. Null when the ctor didn't
    /// request any. Missing names (cookie absent from the jar) are simply not present in
    /// the map.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CapturedAdditionalCookies { get; private set; }

    private async void WebViewLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Per-hoster user-data folder. Persists captcha-solver trust state across runs
            // so the user doesn't have to re-do the hCaptcha challenge every login.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CSUploader",
                "WebView2",
                SanitizeFolderName(_hosterName));
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2EnvironmentOptions options = new();

            string? proxyArg = BuildProxyServerArg(_proxy);
            if (proxyArg is not null)
            {
                // --proxy-server is honoured by Chromium / WebView2 verbatim. Format:
                // "scheme://host:port" (single proxy, no auth). Auth is handled via the
                // BasicAuthenticationRequested event below.
                options.AdditionalBrowserArguments = $"--proxy-server=\"{proxyArg}\"";
            }

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options);

            await WebView.EnsureCoreWebView2Async(env);

            WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            WebView.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;

            // Cookie poll loop. Fires alongside NavigationCompleted because SPA-shaped
            // hosters (FileBoom and friends) log in via XHR with no full-page navigation —
            // their post-login cookie update is invisible to the navigation event. Polling
            // catches it within _pollInterval. Idempotent via _completed.
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = _pollInterval };
            _pollTimer.Tick += async (_, _) => await TryCaptureCookiesAsync();
            _pollTimer.Start();

            // Wire proxy authentication. The 407 challenge from the proxy fires
            // BasicAuthenticationRequested; we answer with the proxy's credentials so
            // Chromium can complete the handshake transparently.
            if (_proxyCredentials is not null)
            {
                WebView.CoreWebView2.BasicAuthenticationRequested += CoreWebView2_BasicAuthenticationRequested;
            }

            _initialized = true;
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["WebViewLogin_Status_Loading_Format"],
                _loginUrl);

            WebView.Source = new Uri(_loginUrl);
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing or corrupt user-data folder — fail loudly so the user
            // knows the WebView isn't going to recover, rather than leaving the window stuck
            // on "Initializing…" forever.
            MessageBox.Show(
                this,
                string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Error_InitFailed_Format"], ex.Message),
                Localizer.Instance["Common_Error"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!_initialized || WebView.CoreWebView2 is null)
        {
            return;
        }

        StatusText.Text = WebView.CoreWebView2.Source ?? string.Empty;
    }

    private void CoreWebView2_BasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        // Fired for HTTP 401 (origin auth) AND 407 (proxy auth). We can't easily tell
        // them apart here without inspecting the URI, but feeding back the proxy
        // credentials on an origin-401 prompt is harmless — the origin will simply
        // reject them and re-prompt, which the user can see in the WebView. The proxy
        // case (the one we actually want to satisfy) succeeds immediately.
        if (_proxyCredentials is null)
        {
            return;
        }

        e.Response.UserName = _proxyCredentials.Username;
        e.Response.Password = _proxyCredentials.Password ?? string.Empty;
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => await TryCaptureCookiesAsync();

    /// <summary>
    /// Reads the cookie jar, applies the validator, and closes the window with the
    /// captured value on first match. Called from BOTH <see cref="CoreWebView2.NavigationCompleted"/>
    /// and the cookie poll timer; the <see cref="_completed"/> guard ensures only the first
    /// caller flips <see cref="Window.DialogResult"/>.
    /// </summary>
    private async Task TryCaptureCookiesAsync()
    {
        if (_completed || WebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            // Cookies are scoped per origin; ask for the cookies the host can see. The
            // CookieManager returns ALL cookies that would be sent on a request to this URL,
            // so subdomain cookies (set on `.ex-load.com`) and host-only cookies both appear.
            // Critically, HttpOnly cookies (FileBoom's accessToken) are included — the
            // HttpOnly flag is a document.cookie restriction, not a CookieManager one.
            System.Collections.Generic.IReadOnlyList<CoreWebView2Cookie> cookies =
                await WebView.CoreWebView2.CookieManager.GetCookiesAsync(_loginUrl);

            CoreWebView2Cookie? sessionCookie = null;
            CoreWebView2Cookie? usernameCookie = null;
            Dictionary<string, string>? additionalCookies = null;
            foreach (CoreWebView2Cookie c in cookies)
            {
                if (string.IsNullOrEmpty(c.Value))
                {
                    continue;
                }
                if (sessionCookie is null && string.Equals(c.Name, _cookieName, StringComparison.Ordinal))
                {
                    sessionCookie = c;
                }
                else if (_usernameCookieName is not null
                    && usernameCookie is null
                    && string.Equals(c.Name, _usernameCookieName, StringComparison.Ordinal))
                {
                    usernameCookie = c;
                }
                else if (_additionalCookieNames is not null)
                {
                    foreach (string name in _additionalCookieNames)
                    {
                        if (string.Equals(c.Name, name, StringComparison.Ordinal))
                        {
                            additionalCookies ??= new(StringComparer.Ordinal);
                            additionalCookies.TryAdd(c.Name, c.Value);
                            break;
                        }
                    }
                }
            }

            if (sessionCookie is null)
            {
                return;
            }

            // Validator opt-in: when set, only close the window if the captured cookie
            // value passes the predicate. Used by hosters whose session cookie is also
            // set in a pre-login bootstrap state (FileBoom issues a client-scoped JWT on
            // first page load and re-issues it user-scoped after password validation —
            // closing on the first sighting would hand back the wrong token).
            if (_cookieValueValidator is not null && !_cookieValueValidator(sessionCookie.Value))
            {
                return;
            }

            if (_completed)
            {
                // Lost the race against another caller — bail without re-flipping
                // DialogResult (which throws once the window has started closing).
                return;
            }
            _completed = true;
            _pollTimer?.Stop();

            CapturedCookieValue = sessionCookie.Value;
            CapturedUsernameCookieValue = usernameCookie?.Value;
            CapturedAdditionalCookies = additionalCookies;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            // Don't tear down the window on a transient cookie-read failure — the next
            // navigation or poll tick will retry. Just surface the diagnostic in the
            // status strip.
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"],
                ex.Message);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void WebViewLoginWindow_Closed(object? sender, EventArgs e)
    {
        // Stop the poll first so no more ticks fire after handlers are detached.
        _pollTimer?.Stop();
        _pollTimer = null;

        // Detach handlers and dispose the WebView so the user-data folder lock is released —
        // otherwise the same hoster's user-data folder stays locked until process exit and
        // a re-open of this window from the same process fails with "the data directory is
        // already in use".
        if (WebView.CoreWebView2 is not null)
        {
            WebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            WebView.CoreWebView2.SourceChanged -= CoreWebView2_SourceChanged;
            WebView.CoreWebView2.BasicAuthenticationRequested -= CoreWebView2_BasicAuthenticationRequested;
        }

        WebView.Dispose();
    }

    /// <summary>
    /// Sanitises a hoster name for use as a directory name (drops chars Windows rejects).
    /// Mirrors the pattern used elsewhere for hoster-keyed paths.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }

        return new string(buffer);
    }

    /// <summary>
    /// Builds the <c>--proxy-server</c> argument value for Chromium. Returns null when
    /// <paramref name="proxy"/> is null, direct, or has no usable WebProxy URI. The
    /// returned string is the <c>scheme://host:port</c> form without credentials —
    /// proxy auth is handled separately via
    /// <see cref="CoreWebView2.BasicAuthenticationRequested"/>.
    /// </summary>
    internal static string? BuildProxyServerArg(ProxyChoice? proxy)
    {
        if (proxy is null || proxy.Id == 0 || proxy.WebProxy is null)
        {
            return null;
        }

        // ProxyChoice.Description is already "scheme://host:port" by construction in
        // ProxyManager — Chromium accepts that format verbatim for the --proxy-server
        // arg. We use the Description rather than re-deriving from the IWebProxy so
        // we get the user-friendly scheme name (e.g. socks5 vs https) without having to
        // re-classify here.
        return proxy.Description;
    }
}

/// <summary>
/// Username/password pair for HTTP/HTTPS proxy authentication. Passed to
/// <see cref="WebViewLoginWindow"/> when the pinned proxy needs auth so the embedded
/// browser can respond to the 407 challenge without a UI prompt.
/// </summary>
public sealed record ProxyCredentials(string Username, string? Password);
