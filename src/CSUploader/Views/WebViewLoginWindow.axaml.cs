// <copyright file="WebViewLoginWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services; // InteractiveAuthResult
using Microsoft.Web.WebView2.Core;

namespace CSUploader.Views;

/// <summary>
/// Modal browser to capture a session cookie / probe value from a captcha-gated hoster (port of WPF
/// src/Views/WebViewLoginWindow.xaml.cs). Hosts a <see cref="CoreWebView2Controller"/> in a native child
/// HWND (<see cref="WebView2Host"/>) — there is no Avalonia WebView2 CONTROL, so this window owns the
/// controller, its bounds (DIP x RenderScaling, the Phase 2 spike recipe), and teardown (controller.Close()
/// releases the per-hoster user-data-folder lock). It also drives completion/capture (Task 4) and the focus
/// integration the WPF WebView2 control did internally (Task 5: MoveFocusRequested / focus-on-activation /
/// initial focus).
/// </summary>
public partial class WebViewLoginWindow : Window
{
    private readonly WebViewLoginViewModel _vm = new();
    private readonly string _hosterName;
    private readonly string _loginUrl;
    private readonly string _cookieName;
    private readonly string? _usernameCookieName;
    private readonly Func<string, bool>? _cookieValueValidator;
    private readonly IReadOnlyList<string>? _additionalCookieNames;
    private readonly string? _successProbeScript;
    private readonly string? _cookieCaptureUrl;
    private readonly string? _userAgentOverride;
    private readonly bool _allowInvalidCertificates;
    private readonly bool _captureOnlyAfterLeavingLoginPage;
    private readonly ProxyChoice? _proxy;
    private readonly ProxyCredentials? _proxyCredentials;

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _creating;
    private bool _torndown;
    private bool _completed;
    private bool _initialFocusPending;
    private DispatcherTimer? _pollTimer;
    private System.Drawing.Rectangle _lastBounds;

    /// <summary>Poll cadence. XFS-family hosters complete via POST->302 (NavigationCompleted already catches
    /// the cookie), but SPA hosters (FileBoom) log in via XHR + history.pushState with no NavigationCompleted,
    /// so the poll is their ONLY signal. 1 s balances latency vs cookie-store read pressure. (WPF: 1 s.)</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001). The app always uses the
    // full overload; this default constructs a harmless empty-spec window that never signs anything in.
    public WebViewLoginWindow()
        : this("(preview)", "about:blank", string.Empty, "__never__")
    {
    }

    public WebViewLoginWindow(
        string hosterName,
        string loginUrl,
        string cookieDomain,
        string cookieName,
        string? usernameCookieName = null,
        ProxyChoice? proxy = null,
        ProxyCredentials? proxyCredentials = null,
        Func<string, bool>? cookieValueValidator = null,
        IReadOnlyList<string>? additionalCookieNames = null,
        string? successProbeScript = null,
        string? cookieCaptureUrl = null,
        string? userAgentOverride = null,
        bool allowInvalidCertificates = false,
        bool captureOnlyAfterLeavingLoginPage = false)
    {
        _hosterName = hosterName;
        _loginUrl = loginUrl;
        _ = cookieDomain; // informational on the spec; the WebView reads cookies by origin (matches WPF)
        _cookieName = cookieName;
        _usernameCookieName = usernameCookieName;
        _proxy = proxy;
        _proxyCredentials = proxyCredentials;
        _cookieValueValidator = cookieValueValidator;
        _additionalCookieNames = additionalCookieNames;
        _successProbeScript = successProbeScript;
        _cookieCaptureUrl = cookieCaptureUrl;
        _userAgentOverride = userAgentOverride;
        _allowInvalidCertificates = allowInvalidCertificates;
        _captureOnlyAfterLeavingLoginPage = captureOnlyAfterLeavingLoginPage;

        InitializeComponent();
        DataContext = _vm;

        _vm.Header = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Header_Format"], hosterName);
        _vm.Status = Localizer.Instance["WebViewLogin_Status_Initializing"];

        Host.HwndReady += OnHwndReady;
        Host.HwndDestroying += TeardownController;

        // Bounds sync: Phase 2 spike recipe. Layout changes + window moves + a pure DPI change (drag to a
        // differently-scaled monitor — the 125%/150% DPI test) which changes RenderScaling with NO layout pass.
        Host.LayoutUpdated += (_, _) => SyncBounds();
        PositionChanged += (_, _) =>
        {
            SyncBounds();
            _controller?.NotifyParentWindowPositionChanged();
        };
        ScalingChanged += (_, _) => SyncBounds();

        Closed += (_, _) => TeardownController();

        // Focus-on-activation (ADAPTATION ADDITION): alt-tabbing back into the login window pushes keyboard
        // focus into the page. Guarded — an Activated that fires before the controller exists is a no-op; the
        // explicit initial-focus (below, after the first navigation) covers the first show.
        Activated += OnWindowActivated;

        // Loop-closer: when Avalonia focus lands back on the host (Tab off the Cancel button), hand it into the
        // page — completing the page <-> Cancel tab loop opened by MoveFocusRequested below.
        Host.GotFocus += OnHostGotFocus;
    }

    // NOTE: InitializeComponent() is emitted by the Avalonia source generator (partial class + WebViewLoginWindow.axaml) —
    // do NOT hand-write it (that is CS0111). This matches EditAccountWindow / MessageBoxWindow.

    // ---- Controller lifecycle (mirrors WebViewLoginWindow.xaml.cs:148-266 + the spike's OnHwndReady) -------

    private async void OnHwndReady(IntPtr hwnd)
    {
        if (_creating || _controller is not null)
        {
            return;
        }

        _creating = true;
        try
        {
            // Per-hoster user-data folder — persists captcha-solver trust across runs so the user need not
            // re-solve hCaptcha every login; per-hoster so two hosters can't leak cookies into each other.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CSUploader", "WebView2", WebViewLoginProxy.SanitizeFolderName(_hosterName));
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2EnvironmentOptions options = new();
            string? proxyArg = WebViewLoginProxy.BuildProxyServerArg(_proxy);
            if (proxyArg is not null)
            {
                options.AdditionalBrowserArguments = $"--proxy-server=\"{proxyArg}\"";
            }

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder, options: options);

            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);

            // Init-in-flight teardown race: if the user Cancelled / closed the window DURING the awaits
            // above, TeardownController already ran with _controller == null (a no-op), so this
            // just-created controller would never be Close()d and its per-hoster user-data-folder lock
            // would leak until process exit (the WPF-documented failure — WebViewLoginWindow.xaml.cs:515-518,
            // "data directory already in use" on the next sign-in). Close it here and bail before wiring up.
            if (_torndown)
            {
                _controller.Close();
                _controller = null;
                return;
            }

            _core = _controller.CoreWebView2;

            // Tab-out of the page (ADAPTATION ADDITION): when the WebView asks to move focus out (Tab past the
            // last field = Next, Shift+Tab before the first = Previous), move Avalonia focus to the only other
            // focusable — the Cancel button — and mark handled so the WebView doesn't beep. Tabbing off Cancel
            // returns to Host (its GotFocus handler pushes focus back into the page). Detached in teardown.
            _controller.MoveFocusRequested += OnControllerMoveFocusRequested;
            _initialFocusPending = true;

            // Pin the UA before any navigation when the spec asks (Cloudflare cf_clearance binds to the exact
            // solving UA — TakeFile).
            if (!string.IsNullOrEmpty(_userAgentOverride))
            {
                _core.Settings.UserAgent = _userAgentOverride;
            }

            _core.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _core.SourceChanged += CoreWebView2_SourceChanged;

            // Completion poll (Avalonia DispatcherTimer, stopped-ctor + explicit Start). Fires alongside
            // NavigationCompleted because SPA-shaped hosters change post-login state with no navigation event.
            // Reached only past the _torndown abort guard above, so it never arms on an already-closed window;
            // teardown stops+nulls it. Idempotent via _completed.
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
            _pollTimer.Tick += async (_, _) => await PollForCompletionAsync();
            _pollTimer.Start();

            if (_proxyCredentials is not null)
            {
                _core.BasicAuthenticationRequested += CoreWebView2_BasicAuthenticationRequested;
            }

            if (_allowInvalidCertificates)
            {
                _core.ServerCertificateErrorDetected += CoreWebView2_ServerCertificateErrorDetected;
            }

            // Drop any persisted *session* cookie before navigating (the Hxfile finding): a stale one would be
            // captured the instant the page loads and close the window before a fresh login, handing the
            // pipeline an anonymous session. Symmetric with the capture read. Safe for every hoster (a cleared
            // profile == a first-ever sign-in); FileBoom's pre-login JWT is rejected by its validator anyway.
            _core.CookieManager.DeleteCookies(_cookieName, _loginUrl);

            // In UA-override (cf_clearance) mode ALSO drop the supplementary cookies — cf_clearance is bound to
            // the solving UA; a value persisted under the native UA would be captured stale.
            if (!string.IsNullOrEmpty(_userAgentOverride) && _additionalCookieNames is not null)
            {
                foreach (string name in _additionalCookieNames)
                {
                    _core.CookieManager.DeleteCookies(name, _loginUrl);
                }
            }

            _lastBounds = default;
            SyncBounds();

            _vm.IsInitialized = true;
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_Loading_Format"], _loginUrl);
            _core.Navigate(_loginUrl);
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing / corrupt user-data folder — fail loudly (custom message box, design's
            // "MessageBox on init-failure -> custom message box") then close with no result. But if the window
            // was torn down / closed mid-init (the race above, or the child HWND was destroyed so the controller
            // create threw), the owner is gone: ShowErrorAsync(this) / Close(null) would throw and escape this
            // async void unobserved — so only surface the error on a still-live window.
            if (!_torndown && IsVisible)
            {
                await MessageBoxWindow.ShowErrorAsync(
                    this,
                    string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Error_InitFailed_Format"], ex.Message),
                    Localizer.Instance["Common_Error"]);
                Close(null);
            }
        }
        finally
        {
            _creating = false;
        }
    }

    private void TeardownController()
    {
        // Terminal: also the "abort" signal an in-flight OnHwndReady checks after CreateAsync resumes,
        // so a controller created after this ran is Closed there instead of leaking its folder lock.
        _torndown = true;

        // Stop the completion poll first: a tick queued behind this teardown must not run a capture against
        // a controller we're about to Close (its post-await guards also re-check _torndown, belt-and-braces).
        _pollTimer?.Stop();
        _pollTimer = null;

        // Focus handlers detach (parity with the _core detaches below; Task 3 review precedent). The Activated /
        // Host.GotFocus handlers are _controller?.-guarded no-ops post-teardown, but detaching keeps teardown
        // symmetric and drops the window's self-references as it closes. MoveFocusRequested lives on the
        // controller, so detach it while it's still non-null (before the Close below).
        Activated -= OnWindowActivated;
        Host.GotFocus -= OnHostGotFocus;
        if (_controller is not null)
        {
            _controller.MoveFocusRequested -= OnControllerMoveFocusRequested;
        }

        if (_core is not null)
        {
            _core.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            _core.SourceChanged -= CoreWebView2_SourceChanged;
            _core.BasicAuthenticationRequested -= CoreWebView2_BasicAuthenticationRequested;
            _core.ServerCertificateErrorDetected -= CoreWebView2_ServerCertificateErrorDetected;
        }

        try
        {
            _controller?.Close(); // releases the per-hoster user-data-folder lock (spike verify d)
        }
        catch
        {
            // Best-effort — the window is closing regardless.
        }

        _controller = null;
        _core = null;
    }

    // ---- Bounds sync (Phase 2 spike recipe: source of truth = host DIP x RenderScaling) --------------------

    private void SyncBounds()
    {
        if (_controller is null)
        {
            return;
        }

        double scaling = RenderScaling;
        int w = Math.Max(1, (int)Math.Round(Host.Bounds.Width * scaling));
        int h = Math.Max(1, (int)Math.Round(Host.Bounds.Height * scaling));

        System.Drawing.Rectangle bounds = new(0, 0, w, h);
        if (bounds != _lastBounds)
        {
            _controller.Bounds = bounds;
            _lastBounds = bounds;
        }
    }

    // ---- Navigation -> VM (completion/capture is Task 4) --------------------------------------------------

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!_vm.IsInitialized || _core is null)
        {
            return;
        }

        _vm.Status = _core.Source ?? string.Empty;
        _vm.LastNavigationUrl = _core.Source;
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _vm.RecordNavigationCompleted(_core?.Source);

        // Initial focus one-shot (ADAPTATION ADDITION): after the first page finishes loading, push keyboard
        // focus into it so the user can type without a mouse click first. _controller?.-guarded (teardown nulls
        // the controller, so this never runs against a Closed one).
        if (_initialFocusPending)
        {
            _initialFocusPending = false;
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }

        await PollForCompletionAsync();
    }

    private void CoreWebView2_BasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        // Fires for 401 (origin) AND 407 (proxy). Feeding proxy creds on a 401 is harmless (the origin rejects
        // + re-prompts, visible in the WebView); the 407 case — the one we want — succeeds immediately.
        if (_proxyCredentials is null)
        {
            return;
        }

        e.Response.UserName = _proxyCredentials.Username;
        e.Response.Password = _proxyCredentials.Password ?? string.Empty;
    }

    private void CoreWebView2_ServerCertificateErrorDetected(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
        // AlwaysAllow == the C# handler's DangerousAcceptAnyServerCertificateValidator; only ever reached when
        // the user explicitly enabled AllowInvalidServerCertificates.
        => e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    // ---- Focus integration (ADAPTATION ADDITION; the spike never exercised focus) -------------------------
    // Hosting a raw CoreWebView2Controller (vs the WPF WebView2 CONTROL, which bridges focus internally) means
    // this window must carry keyboard focus across the native<->Avalonia boundary itself: INTO the page on
    // window activation + the initial navigation, and OUT to the Cancel button on Tab-out. All three handlers
    // are detached in TeardownController (parity with the _core handlers). Every MoveFocus is _controller?.-
    // guarded — teardown nulls the controller in the same synchronous block it Closes it, so a non-null
    // controller is never a Closed one.

    private void OnWindowActivated(object? sender, EventArgs e)
        => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    private void OnHostGotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
        => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    private void OnControllerMoveFocusRequested(object? sender, CoreWebView2MoveFocusRequestedEventArgs e)
    {
        if (e.Reason is CoreWebView2MoveFocusReason.Next or CoreWebView2MoveFocusReason.Previous)
        {
            CancelButton.Focus();
            e.Handled = true;
        }
    }

    // ---- Completion / capture (mirrors WebViewLoginWindow.xaml.cs:302-502; single-completion guard = rule 49) --

    /// <summary>Per-tick check: the JS probe for probe-script hosters (HitFile), else the cookie-jar read.</summary>
    private Task PollForCompletionAsync()
        => _successProbeScript is not null ? TryProbeAsync() : TryCaptureCookiesAsync();

    private async Task TryCaptureCookiesAsync()
    {
        // _torndown short-circuit (Task 3 race guard): a tick / NavigationCompleted queued behind teardown must
        // not read a Closed controller. _core is nulled by teardown too, but naming _torndown documents intent.
        if (_completed || _torndown || _core is null)
        {
            return;
        }

        // Some XFS hosters (KatFile) set the session cookie on the login page BEFORE authentication — wait for
        // the post-login navigation so we don't capture a guest session and close the window too early.
        if (_captureOnlyAfterLeavingLoginPage
            && WebViewLoginCapture.IsOnLoginPage(_vm.LastNavigationUrl, _loginUrl))
        {
            return;
        }

        try
        {
            // CookieManager returns ALL cookies a request to _loginUrl would send — incl. HttpOnly (FileBoom's
            // accessToken); the HttpOnly flag only gates document.cookie, not CookieManager.
            IReadOnlyList<CoreWebView2Cookie> cookies = await _core.CookieManager.GetCookiesAsync(_loginUrl);

            CookieSelection sel = WebViewLoginCapture.SelectCookies(
                cookies.Select(c => (c.Name, c.Value)),
                _cookieName, _usernameCookieName, _additionalCookieNames, _cookieValueValidator);

            // BINDING forward contract (Task 1 reviewer): consumption gates PURELY on SessionValue. On the
            // validator-reject path SelectCookies still returns UsernameValue/AdditionalCookies (the values WPF
            // discarded there), so we MUST ignore them while SessionValue is null and simply keep polling — a
            // later successful poll recomputes all three together. Only PAST this guard, with a non-null
            // SessionValue, are sel.UsernameValue / sel.AdditionalCookies trustworthy for the result below.
            // (_completed / _torndown also bail: another caller won the race, or the window is tearing down.)
            if (sel.SessionValue is null || _completed || _torndown)
            {
                return;
            }

            _completed = true;
            _pollTimer?.Stop();
            _vm.IsCompleted = true;
            Close(new InteractiveAuthResult(sel.SessionValue, sel.UsernameValue, sel.AdditionalCookies));
        }
        catch (Exception ex)
        {
            // Transient cookie-read failure — the next nav/poll retries; just surface the diagnostic.
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"], ex.Message);
        }
    }

    private async Task TryProbeAsync()
    {
        if (_completed || _torndown || _successProbeScript is null || _core is null)
        {
            return;
        }

        try
        {
            string raw = await _core.ExecuteScriptAsync(_successProbeScript);
            string? value = WebViewLoginCapture.TryParseJsonString(raw);
            if (string.IsNullOrEmpty(value) || _completed || _torndown)
            {
                return; // page not authenticated yet (or lost the race / torn down)
            }

            _completed = true;
            _pollTimer?.Stop();
            _vm.IsCompleted = true;

            // Probe hosters can ALSO ask us to hand the logged-in cookie jar to the C# side (HitFile refresh).
            // HttpOnly included (CookieManager, not document.cookie). Best-effort — a failure here must not
            // block an otherwise-successful sign-in.
            string? cookieHeader = null;
            if (_cookieCaptureUrl is not null && !_torndown && _core is not null)
            {
                try
                {
                    IReadOnlyList<CoreWebView2Cookie> jar = await _core.CookieManager.GetCookiesAsync(_cookieCaptureUrl);
                    cookieHeader = WebViewLoginCapture.BuildCookieHeader(jar.Select(c => (c.Name, c.Value)));
                }
                catch
                {
                    // Leave cookieHeader null — sign-in still succeeds via the probe value.
                }
            }

            // The identity cookie is read here too, NOT only on the cookie path. This used to pass null
            // unconditionally, which made UsernameCookieName silently dead config for every probe hoster:
            // FileStore asked for XFS's `login` cookie, got nothing, and its accounts saved nameless — then
            // displayed as "https:**", DisplayName's masked-key fallback chewing on a node URL.
            string? username = await TryReadUsernameCookieAsync();

            if (_torndown)
            {
                return; // window torn down during the (awaited) cookie-jar read — don't Close a dead window
            }

            Close(new InteractiveAuthResult(cookieHeader ?? string.Empty, username, null, value));
        }
        catch (Exception ex)
        {
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"], ex.Message);
        }
    }

    /// <summary>
    /// The value of the spec's <c>UsernameCookieName</c>, or null when the spec asked for none, the
    /// cookie isn't in the jar, or the read failed. Best-effort by design: a missing name costs a
    /// label, and must never cost the sign-in the user just completed.
    /// </summary>
    private async Task<string?> TryReadUsernameCookieAsync()
    {
        if (_usernameCookieName is null || _core is null || _torndown)
        {
            return null;
        }

        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies = await _core.CookieManager.GetCookiesAsync(_loginUrl);
            return WebViewLoginCapture.SelectCookies(
                cookies.Select(c => (c.Name, c.Value)),
                _cookieName,
                _usernameCookieName,
                additionalCookieNames: null,
                cookieValueValidator: null).UsernameValue;
        }
        catch
        {
            return null;
        }
    }
}
