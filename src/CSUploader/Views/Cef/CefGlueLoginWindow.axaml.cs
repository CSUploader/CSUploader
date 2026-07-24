// <copyright file="CefGlueLoginWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if !WINDOWS
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services; // InteractiveAuthResult, CefBootstrap
using CSUploader.Views;    // WebViewLoginViewModel, WebViewLoginCapture, WebViewLoginProxy, ProxyCredentials, CefProbeResult, CefNavigationSequencer
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Events;

namespace CSUploader.Views.Cef;

/// <summary>
/// Modal browser to capture a session cookie / probe value from a captcha-gated hoster on the NON-Windows head
/// — the CefGlue/CEF analog of <see cref="WebViewLoginWindow"/>. Hosts an <see cref="AvaloniaCefBrowser"/>
/// bound to a PER-LOGIN <see cref="CefRequestContext"/> (own cache path under the CEF bootstrap root); the
/// proxy preference, cookie reads and cookie deletes all go through THAT context's manager, never the global
/// jar (isolation). Reuses the engine-agnostic helpers unchanged: <see cref="WebViewLoginViewModel"/> (nav
/// state), <see cref="WebViewLoginCapture"/> (cookie selection / jar serialization) and
/// <see cref="WebViewLoginProxy"/> (proxy classification). Completion/capture, the <c>_completed</c>/
/// <c>_torndown</c> latches and <c>Close(result)</c> port from the WebView2 window; the CEF specifics
/// (async cookie visitor on the IO thread, DevTools UA override, async teardown that awaits <c>OnBeforeClose</c>)
/// live here. Excluded from the Windows compile.
/// </summary>
public partial class CefGlueLoginWindow : Window
{
    /// <summary>Poll cadence — mirrors the WebView2 window. XFS-family hosters complete via POST→302 (a nav
    /// event already catches the cookie), but SPA hosters log in via XHR with no nav event, so the poll is
    /// their only signal.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Zero-result safety net: CEF never invokes the cookie visitor for a URL with no cookies, so the
    /// awaiting TCS would otherwise hang (the spike's finding).</summary>
    private static readonly TimeSpan CookieVisitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Safety net around the async <c>DeleteCookies</c> callback so a never-firing delete can't wedge
    /// the pre-navigation sequence.</summary>
    private static readonly TimeSpan DeleteCookieTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Cap on the async teardown wait for <c>OnBeforeClose</c> so a wedged close can't hang the app.</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);

    private readonly WebViewLoginViewModel _vm = new();
    private readonly TaskCompletionSource _beforeCloseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string _hosterName = "(preview)";
    private string _loginUrl = "about:blank";
    private string _cookieName = "__never__";
    private string? _usernameCookieName;
    private Func<string, bool>? _cookieValueValidator;
    private IReadOnlyList<string>? _additionalCookieNames;
    private string? _successProbeScript;
    private string? _wrappedProbeScript;
    private string? _cookieCaptureUrl;
    private string? _userAgentOverride;
    private bool _allowInvalidCertificates;
    private bool _captureOnlyAfterLeavingLoginPage;
    private ProxyChoice? _proxy;
    private ProxyCredentials? _proxyCredentials;

    private AvaloniaCefBrowser? _browser;
    private CefRequestContext? _requestContext;
    private CefLoginLifeSpanHandler? _lifeSpanHandler;

    // Written on the CEF UI thread (OnAfterCreated), read on the Avalonia UI thread (teardown) — volatile for
    // cross-thread visibility. _torndown is likewise read on the CEF UI thread by the init-in-flight guard.
    private volatile CefBrowser? _cefBrowser;
    private volatile bool _torndown;

    private bool _completed;          // UI-thread only (poll / nav)
    private bool _navigationStarted;  // UI-thread only; gates completion checks until the stale cookie is gone
    private DispatcherTimer? _pollTimer;

    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001). Constructs a harmless
    // window that NEVER creates a browser or signs anything in — the app always uses the full overload.
    public CefGlueLoginWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Header = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Header_Format"], _hosterName);
        _vm.Status = Localizer.Instance["WebViewLogin_Status_Initializing"];
    }

    public CefGlueLoginWindow(
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
        _ = cookieDomain; // informational on the spec; cookies are read by origin (matches the WebView2 head)
        _cookieName = cookieName;
        _usernameCookieName = usernameCookieName;
        _proxy = proxy;
        _proxyCredentials = proxyCredentials;
        _cookieValueValidator = cookieValueValidator;
        _additionalCookieNames = additionalCookieNames;
        _successProbeScript = successProbeScript;
        _wrappedProbeScript = CefProbeResult.WrapProbeScript(successProbeScript);
        _cookieCaptureUrl = cookieCaptureUrl;
        _userAgentOverride = userAgentOverride;
        _allowInvalidCertificates = allowInvalidCertificates;
        _captureOnlyAfterLeavingLoginPage = captureOnlyAfterLeavingLoginPage;

        InitializeComponent();
        DataContext = _vm;

        _vm.Header = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Header_Format"], hosterName);
        _vm.Status = Localizer.Instance["WebViewLogin_Status_Initializing"];

        CreateBrowser();

        // Focus into the page content on open (the OSR/native browser participates in Avalonia focus). Escape
        // is wired via the Cancel button's IsCancel="True"; Cancel/X → Close(null).
        Opened += (_, _) => _browser?.Focus();
        Closed += (_, _) => Teardown();
    }

    // NOTE: InitializeComponent() is emitted by the Avalonia source generator (partial class + the .axaml).

    private void CreateBrowser()
    {
        // Per-login request context with its own cache path under the bootstrap root (stable per hoster so the
        // captcha-solver trust persists across runs, mirroring the WebView2 per-hoster user-data folders). The
        // proxy preference is applied ON THIS CONTEXT once it initializes (context handler), and every cookie
        // op below goes through THIS context's manager — never CefCookieManager.GetGlobal.
        string? proxyServer = WebViewLoginProxy.BuildProxyServerArg(_proxy);
        CefLoginRequestContextHandler contextHandler = new(proxyServer);
        string cachePath = CefBootstrap.LoginCachePathFor(_hosterName);

        _lifeSpanHandler = new CefLoginLifeSpanHandler();
        _lifeSpanHandler.AfterCreated += OnBrowserAfterCreated;
        _lifeSpanHandler.BeforeClose += OnBrowserBeforeClose;

        // The per-login CefRequestContext MUST be created only AFTER CEF is initialized. CefGlue defers CEF init
        // (libcef load + CefRuntime.Initialize) until the FIRST AvaloniaCefBrowser is constructed — its static/base
        // ctor calls CefRuntimeLoader.Load. Creating the context BEFORE the browser would run
        // cef_request_context_create_context against an uninitialized CEF core, returning an invalid native context
        // whose finalizer NREs (cef_preference_manager_t.release → CefPreferenceManager.Finalize), and whose
        // OnRequestContextInitialized never fires so the proxy preference is silently dropped. BaseCefBrowser invokes
        // this factory DURING construction, immediately AFTER it has initialized CEF, so the context is created
        // against a live core. The lambda runs synchronously inside the ctor below, so _requestContext is set on return.
        // NOTE: this stored wrapper is used only to build the browser (isolation) and to dispose at teardown — cookie
        // calls go through the browser's OWN live context (GetLoginCookieManager), never this wrapper (see there).
        _browser = new AvaloniaCefBrowser(() => _requestContext = CefRequestContext.CreateContext(
            new CefRequestContextSettings { CachePath = cachePath, PersistSessionCookies = true },
            contextHandler))
        {
            // OSR keyboard fix (Linux/macOS use offscreen rendering). AvaloniaCefBrowser is a bare Control whose
            // Focusable defaults to FALSE, so it never receives Avalonia keyboard focus — and in OSR the ONLY path
            // that forwards keystrokes into CEF is the control's own KeyDown/KeyUp/TextInput handlers calling
            // SendKeyEvent. Result: the page renders and the mouse works (pointer events don't need focus) but you
            // can't type. Making the control focusable lets a click into a field — and the LoadEnd auto-focus below
            // — route keystrokes. (Windowed builds let CEF's native child window handle keys, and the Windows head
            // uses WebView2, a separate stack — both unaffected. Confirmed against OutSystems/CefGlue OSR source.)
            Focusable = true,
            LifeSpanHandler = _lifeSpanHandler,
            RequestHandler = new CefLoginRequestHandler(_allowInvalidCertificates, _proxyCredentials, _userAgentOverride),
            // Suppress the right-click menu — under OSR on Linux it opens then instantly dismisses (a flash); it
            // has no use in a captcha/login flow and keyboard copy/paste still works.
            ContextMenuHandler = new CefLoginContextMenuHandler(),
        };
        _browser.LoadEnd += OnBrowserLoadEnd;
        _browser.AddressChanged += OnBrowserAddressChanged;

        // Auto-focus the page once content loads so the user can type without first clicking. Focus must land
        // AFTER the CEF browser host exists — Focus() in Window.Opened (below) is too early because the browser is
        // created lazily on first layout, so GotFocus→SetFocus(true) is dropped. LoadEnd fires on the CEF thread;
        // marshal to the UI thread. Idempotent across redirects / sub-frame loads.
        _browser.LoadEnd += (_, _) => Dispatcher.UIThread.Post(() => _browser?.Focus());

        BrowserHost.Child = _browser;
    }

    // ---- Creation / teardown (CEF UI thread callbacks marshal to the Avalonia UI thread) -------------------

    private void OnBrowserAfterCreated(CefBrowser browser)
    {
        _cefBrowser = browser;

        // Init-in-flight race guard (design; the analog of the WebView2 controller-race guard): if the window
        // was torn down DURING browser creation, close the late browser immediately and don't wire anything up.
        if (_torndown)
        {
            try
            {
                browser.GetHost().CloseBrowser(true);
            }
            catch
            {
                // Best-effort — the window is already gone.
            }

            return;
        }

        // UA override must be applied before navigation. The DevTools call is valid on the CEF UI thread (here);
        // the marshaled continuation does the delete-before-navigate + poll arming on the Avalonia UI thread.
        ApplyUserAgentOverride(browser);
        Dispatcher.UIThread.Post(() => _ = OnBrowserReadyAsync());
    }

    private void OnBrowserBeforeClose() => _beforeCloseTcs.TrySetResult();

    private async Task OnBrowserReadyAsync()
    {
        if (_torndown || _browser is null)
        {
            return;
        }

        _vm.IsInitialized = true;
        _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_Loading_Format"], _loginUrl);

        // Drop any persisted *session* cookie before navigating (the Hxfile finding), AWAITING CEF's async
        // delete callback so the wipe can't race the navigation (design R1). Only then start the completion
        // poll — a poll that ran before the delete completed could capture the stale (anonymous) session.
        try
        {
            await CefNavigationSequencer.DeleteThenNavigateAsync(
                deleteCookiesAsync: DeleteStaleCookiesAsync,
                navigate: () =>
                {
                    if (!_torndown && _browser is not null)
                    {
                        _browser.Address = _loginUrl; // CefGlue marshals the LoadUrl to the right frame/thread
                        _navigationStarted = true;
                    }
                });
        }
        catch (Exception ex)
        {
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"], ex.Message);
            return;
        }

        if (_torndown)
        {
            return;
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _pollTimer.Tick += async (_, _) => await PollForCompletionAsync();
        _pollTimer.Start();
    }

    private async void Teardown()
    {
        if (_torndown)
        {
            return;
        }

        // Terminal + the "abort" signal the in-flight OnBrowserAfterCreated re-checks, so a browser created
        // after this is closed there instead of leaking.
        _torndown = true;

        _pollTimer?.Stop();
        _pollTimer = null;

        if (_browser is not null)
        {
            _browser.LoadEnd -= OnBrowserLoadEnd;
            _browser.AddressChanged -= OnBrowserAddressChanged;
        }

        // Async close (design): CloseBrowser(true) → wait OnBeforeClose → release the control. CEF's browser
        // destruction is asynchronous; OnBeforeClose is the signal that the managed-visible browser is gone.
        CefBrowser? cef = _cefBrowser;
        if (cef is not null)
        {
            try
            {
                cef.GetHost().CloseBrowser(true);
            }
            catch
            {
                // Best-effort.
            }

            await Task.WhenAny(_beforeCloseTcs.Task, Task.Delay(TeardownTimeout)).ConfigureAwait(true);
        }

        if (_lifeSpanHandler is not null)
        {
            _lifeSpanHandler.AfterCreated -= OnBrowserAfterCreated;
            _lifeSpanHandler.BeforeClose -= OnBrowserBeforeClose;
        }

        try
        {
            _browser?.Dispose(); // releases the adapter (disposes the OSR render surfaces; browser already closed)
        }
        catch
        {
            // Best-effort — the window is closing regardless.
        }

        _browser = null;

        // Do NOT Dispose() the per-login CefRequestContext here. It is a reference-counted native object and
        // CEF continues an ASYNCHRONOUS teardown of the browser's request context (storage partition / network
        // context / cookie store) on its own threads AFTER OnBeforeClose fires. Our wrapper holds a single
        // native ref; Dispose() releases it synchronously at that moment, which can drop the native refcount to
        // zero while Chromium's async teardown is still in flight — an intermittent use-after-free that
        // SIGSEGVs the whole process on CrBrowserMain (V8 region) under the WSLg software-GL timing. Instead we
        // drop the managed reference and let the finalizer (~CefPreferenceManager → Release) release the native
        // ref at a GC-safe time, well after Chromium's async teardown has completed. Reproduced + verified with
        // a local file:// teardown harness: Dispose()-here fails ~50-80% of closes under stress; dropping the
        // reference is clean across 70+ consecutive open/close cycles. (Cookie ops already use the browser's own
        // live context via GetLoginCookieManager, never this wrapper, so nothing else depends on disposing it.)
        _requestContext = null;
    }

    private void ApplyUserAgentOverride(CefBrowser browser)
    {
        if (string.IsNullOrEmpty(_userAgentOverride))
        {
            return;
        }

        // Per-browser DevTools override so navigator.userAgent + UA client hints (what Cloudflare reads) match
        // the header rewrite in CefLoginResourceRequestHandler. Best-effort: if DevTools is unavailable the
        // header rewrite still presents the UA to the network.
        try
        {
            CefBrowserHost host = browser.GetHost();
            using (CefDictionaryValue enableParams = CefDictionaryValue.Create())
            {
                host.ExecuteDevToolsMethod(1, "Network.enable", enableParams);
            }

            using CefDictionaryValue uaParams = CefDictionaryValue.Create();
            uaParams.SetString("userAgent", _userAgentOverride);
            host.ExecuteDevToolsMethod(2, "Network.setUserAgentOverride", uaParams);
        }
        catch
        {
            // Best-effort.
        }
    }

    // ---- Navigation -> VM + completion poll (nav events fire on the CEF UI thread; marshal to the UI thread) --

    private void OnBrowserAddressChanged(object? sender, string address)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_torndown || !_vm.IsInitialized)
            {
                return;
            }

            _vm.Status = address;
            _vm.LastNavigationUrl = address;
        });

    private void OnBrowserLoadEnd(object? sender, LoadEndEventArgs e)
    {
        if (e.Frame is not { IsMain: true })
        {
            return;
        }

        string url = e.Frame.Url;
        Dispatcher.UIThread.Post(() =>
        {
            if (_torndown)
            {
                return;
            }

            _vm.RecordNavigationCompleted(url);
            _ = PollForCompletionAsync();
        });
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    // ---- Completion / capture (mirrors WebViewLoginWindow; single-completion guard) ------------------------

    private Task PollForCompletionAsync()
        => _wrappedProbeScript is not null ? TryProbeAsync() : TryCaptureCookiesAsync();

    private async Task TryCaptureCookiesAsync()
    {
        if (_completed || _torndown || !_navigationStarted || _browser is null || _requestContext is null)
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
            IReadOnlyList<CefCookie> cookies = await VisitUrlCookiesAsync(_loginUrl);

            CookieSelection sel = WebViewLoginCapture.SelectCookies(
                cookies.Select(c => (c.Name, c.Value)),
                _cookieName, _usernameCookieName, _additionalCookieNames, _cookieValueValidator);

            // Consumption gates PURELY on SessionValue (the WebView2 head's forward contract): while it is null
            // the validator hasn't passed — ignore username/additional and keep polling.
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
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"], ex.Message);
        }
    }

    private async Task TryProbeAsync()
    {
        if (_completed || _torndown || !_navigationStarted || _wrappedProbeScript is null || _browser is null)
        {
            return;
        }

        try
        {
            // CEF returns the value DIRECTLY (not JSON-quoted); the Task-3 shim treats empty/null as "not yet".
            string? evaluated = await _browser.EvaluateJavaScript<string>(_wrappedProbeScript);
            if (!CefProbeResult.TryProbeComplete(evaluated, out string value) || _completed || _torndown)
            {
                return; // page not authenticated yet (or lost the race / torn down)
            }

            _completed = true;
            _pollTimer?.Stop();
            _vm.IsCompleted = true;

            // Probe hosters can ALSO ask us to hand the logged-in cookie jar to the C# side (HitFile refresh).
            // Best-effort — a failure here must not block an otherwise-successful sign-in.
            string? cookieHeader = null;
            if (_cookieCaptureUrl is not null && !_torndown && _requestContext is not null)
            {
                try
                {
                    IReadOnlyList<CefCookie> jar = await VisitUrlCookiesAsync(_cookieCaptureUrl);
                    cookieHeader = WebViewLoginCapture.BuildCookieHeader(jar.Select(c => (c.Name, c.Value)));
                }
                catch
                {
                    // Leave cookieHeader null — sign-in still succeeds via the probe value.
                }
            }

            if (_torndown)
            {
                return;
            }

            Close(new InteractiveAuthResult(cookieHeader ?? string.Empty, null, null, value));
        }
        catch (Exception ex)
        {
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"], ex.Message);
        }
    }

    // ---- CEF cookie plumbing (CEF-UI-thread manager access → IO-thread visitor/delete → TCS → UI marshal) ----

    // Runs an action on the CEF UI thread. CefRequestContext / CefCookieManager / CefBrowserHost calls have CEF
    // UI-thread affinity, and CefGlue forces MultiThreadedMessageLoop on Linux, so CEF's UI thread is a dedicated
    // native thread — NOT the Avalonia UI thread these methods are otherwise invoked from.
    private static void RunOnCefUiThread(Action action)
    {
        if (CefRuntime.CurrentlyOn(CefThreadId.UI))
        {
            action();
        }
        else
        {
            CefRuntime.PostTask(CefThreadId.UI, new CefActionTask(action));
        }
    }

    // Returns the login's cookie manager, sourced from the LIVE browser's request context — NOT the stored
    // _requestContext wrapper. That wrapper's native pointer is released out from under us during CEF's browser
    // setup (its managed lifetime is not ours to control), so calling GetCookieManager on it dereferences a freed
    // context and segfaults. The browser host's own request context (GetHost().GetRequestContext()) is always the
    // live per-login context (IsGlobal == false — isolation preserved). Must be called on the CEF UI thread.
    private CefCookieManager? GetLoginCookieManager()
    {
        CefBrowser? browser = _cefBrowser;
        if (browser is null)
        {
            return null;
        }

        using CefRequestContext context = browser.GetHost().GetRequestContext();
        return context.GetCookieManager(null); // the manager holds its own ref; the context wrapper can be released
    }

    private Task<IReadOnlyList<CefCookie>> VisitUrlCookiesAsync(string url)
    {
        TaskCompletionSource<IReadOnlyList<CefCookie>> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CefLoginCookieCollector visitor = new(tcs);

        RunOnCefUiThread(() =>
        {
            try
            {
                // includeHttpOnly:true — FileBoom's accessToken / XFS session cookies are HttpOnly; the
                // CookieManager (not document.cookie) exposes them.
                CefCookieManager? manager = GetLoginCookieManager();
                if (manager is null || !manager.VisitUrlCookies(url, includeHttpOnly: true, visitor))
                {
                    tcs.TrySetResult(Array.Empty<CefCookie>());
                }
            }
            catch
            {
                tcs.TrySetResult(Array.Empty<CefCookie>()); // torn down / disposed browser — treat as no cookies
            }
        });

        // Zero-result net: a cookieless URL never triggers Visit, so the TCS would hang without this.
        _ = Task.Delay(CookieVisitTimeout).ContinueWith(
            _ => tcs.TrySetResult(Array.Empty<CefCookie>()), TaskScheduler.Default);

        return tcs.Task;
    }

    private Task DeleteStaleCookiesAsync()
    {
        List<string> names = [_cookieName];

        // In UA-override (cf_clearance) mode also drop the supplementary cookies — a value persisted under the
        // native UA would be captured stale (mirrors the WebView2 head).
        if (!string.IsNullOrEmpty(_userAgentOverride) && _additionalCookieNames is not null)
        {
            names.AddRange(_additionalCookieNames);
        }

        return Task.WhenAll(names.Select(DeleteCookieAsync));
    }

    private Task DeleteCookieAsync(string cookieName)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CefLoginDeleteCookiesCallback callback = new(tcs);

        RunOnCefUiThread(() =>
        {
            try
            {
                CefCookieManager? manager = GetLoginCookieManager();
                if (manager is null || !manager.DeleteCookies(_loginUrl, cookieName, callback))
                {
                    tcs.TrySetResult();
                }
            }
            catch
            {
                tcs.TrySetResult(); // torn down / disposed browser — nothing to delete
            }
        });

        _ = Task.Delay(DeleteCookieTimeout).ContinueWith(
            _ => tcs.TrySetResult(), TaskScheduler.Default);

        return tcs.Task;
    }
}
#endif
