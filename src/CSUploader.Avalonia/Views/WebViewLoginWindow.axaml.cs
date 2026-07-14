// <copyright file="WebViewLoginWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
/// releases the per-hoster user-data-folder lock). Completion is Task 4; focus integration is Task 5.
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
    private readonly ProxyChoice? _proxy;
    private readonly ProxyCredentials? _proxyCredentials;

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _creating;
    private bool _torndown;
    private System.Drawing.Rectangle _lastBounds;

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
        bool allowInvalidCertificates = false)
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

            // Pin the UA before any navigation when the spec asks (Cloudflare cf_clearance binds to the exact
            // solving UA — TakeFile).
            if (!string.IsNullOrEmpty(_userAgentOverride))
            {
                _core.Settings.UserAgent = _userAgentOverride;
            }

            _core.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _core.SourceChanged += CoreWebView2_SourceChanged;

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

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => _vm.RecordNavigationCompleted(_core?.Source);

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
}
