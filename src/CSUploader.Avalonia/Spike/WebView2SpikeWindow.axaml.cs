// THROWAWAY — Phase 2 WebView2 spike; superseded by the Phase 8 login host.
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Web.WebView2.Core;

namespace CSUploader.Spike;

/// <summary>
/// GO/NO-GO spike: hosts a WebView2 <c>CoreWebView2Controller</c> in a native child HWND
/// (<see cref="WebView2HwndHost"/>) inside an Avalonia window, with a diagnostics panel of
/// bridge-readable Avalonia controls. Every observation the agent needs surfaces on the
/// Avalonia side (status/bounds TextBlocks, ProbeOutput TextBox) because the native HWND is
/// invisible to bridge screenshots — <see cref="CoreWebView2.CapturePreviewAsync"/> is the eye
/// inside the WebView. See docs/superpowers/specs/2026-07-11-webview2-spike-verdict.md.
/// </summary>
public partial class WebView2SpikeWindow : Window
{
    // Scratch user-data folder (NOT the app's real %LOCALAPPDATA%\CSUploader\WebView2 tree) so
    // verify point (d) can rename/delete it after the window closes to prove the lock released.
    private const string UserDataFolder = @"D:\temp2\cbuild-mig\webview2-udf";
    private const string CaptureDir = @"D:\temp2\cbuild-mig\shots";

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _creating;
    private Rectangle _lastBounds;

    public WebView2SpikeWindow()
    {
        InitializeComponent();

        Host.HwndReady += OnHwndReady;
        Host.HwndDestroying += TeardownController;

        GoButton.Click += (_, _) => Navigate(UrlBox.Text);
        FocusButton.Click += async (_, _) => await FocusFirstFieldAsync();
        ProbeButton.Click += async (_, _) => await ProbeAsync();
        CookiesButton.Click += async (_, _) => await CookiesAsync();
        CaptureButton.Click += async (_, _) => await CaptureAsync();

        // Bounds sync: on layout changes and window moves. NotifyParentWindowPositionChanged
        // tells the WebView its parent moved on screen (repaint/hit-test correctness).
        Host.LayoutUpdated += (_, _) => SyncBounds();
        PositionChanged += (_, _) =>
        {
            SyncBounds();
            _controller?.NotifyParentWindowPositionChanged();
        };

        Closed += (_, _) => TeardownController();
    }

    // ------------------------------------------------------------------
    // Controller lifecycle
    // ------------------------------------------------------------------

    private async void OnHwndReady(IntPtr hwnd)
    {
        if (_creating || _controller is not null)
        {
            return;
        }

        _creating = true;
        try
        {
            Directory.CreateDirectory(UserDataFolder);

            // Environment creation transplanted from WebViewLoginWindow.xaml.cs:172-175
            // (browserExecutableFolder: null = the installed Evergreen runtime).
            CoreWebView2EnvironmentOptions options = new();
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: UserDataFolder,
                options: options);

            // NEW code for the spike (confirmed present on 1.0.4022.49): parent the WebView to
            // our bare child HWND rather than handing the env to a WebView2 control.
            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            _core = _controller.CoreWebView2;

            _core.NavigationStarting += (_, e) => SetStatus($"NavigationStarting → {e.Uri}");
            _core.NavigationCompleted += (_, e) =>
                SetStatus($"NavigationCompleted (success={e.IsSuccess}, err={e.WebErrorStatus}) → {_core?.Source}");
            _core.SourceChanged += (_, _) => SetStatus($"SourceChanged → {_core?.Source}");

            _lastBounds = default;
            SyncBounds();
            SetStatus("Controller created.");
            Navigate(UrlBox.Text);
        }
        catch (Exception ex)
        {
            SetStatus($"Init FAILED: {ex.Message}");
            ProbeOutput.Text = ex.ToString();
        }
        finally
        {
            _creating = false;
        }
    }

    private void TeardownController()
    {
        try
        {
            _controller?.Close();
        }
        catch
        {
            // Best-effort teardown — the spike is closing regardless.
        }

        _controller = null;
        _core = null;
    }

    // ------------------------------------------------------------------
    // Bounds sync
    // ------------------------------------------------------------------

    private void SyncBounds()
    {
        if (_controller is null)
        {
            return;
        }

        double scaling = RenderScaling;
        double dipW = Host.Bounds.Width;
        double dipH = Host.Bounds.Height;

        // Source of truth = the Avalonia control's laid-out DIP bounds × RenderScaling (the
        // plan-specified conversion). The child HWND's GetClientRect is read ONLY for the
        // diagnostic readout: it can lag a resize (observed at 100% DPI — width tracked but height
        // stayed stale), so it must NOT drive the bounds or the WebView overflows the host.
        int boundsW = Math.Max(1, (int)Math.Round(dipW * scaling));
        int boundsH = Math.Max(1, (int)Math.Round(dipH * scaling));
        bool haveRect = Host.TryGetChildClientSize(out int cw, out int ch);

        Rectangle bounds = new(0, 0, boundsW, boundsH);
        if (bounds != _lastBounds)
        {
            _controller.Bounds = bounds;
            _lastBounds = bounds;
        }

        BoundsText.Text =
            $"host DIP {dipW:0}x{dipH:0} | scaling {scaling:0.00} | controller.Bounds {boundsW}x{boundsH} | " +
            $"childRect(diag) {(haveRect ? $"{cw}x{ch}" : "n/a")}";
    }

    // ------------------------------------------------------------------
    // Diagnostics actions (surface everything to bridge-readable controls)
    // ------------------------------------------------------------------

    private void Navigate(string? url)
    {
        if (_core is null)
        {
            SetStatus("Navigate: no controller yet.");
            return;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            _core.Navigate(url);
            SetStatus("Navigating → " + url);
        }
        catch (Exception ex)
        {
            SetStatus("Navigate failed: " + ex.Message);
        }
    }

    /// <summary>Verify point (a) helper: move Win32 focus into the WebView and JS-focus the first
    /// text-like field, so a subsequent native SendInput proves keystrokes reach the page.</summary>
    private async Task FocusFirstFieldAsync()
    {
        if (_core is null || _controller is null)
        {
            SetStatus("Focus: no controller.");
            return;
        }

        _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        const string js =
            "(function(){var el=document.querySelector('input[type=text],input[type=email],input[type=search],input:not([type])');" +
            "if(!el)return 'no-text-input-found';el.focus();var a=document.activeElement;" +
            "return (a.tagName||'')+' name='+(a.getAttribute?a.getAttribute('name'):'')+' id='+(a.id||'');})()";
        try
        {
            string raw = await _core.ExecuteScriptAsync(js);
            ProbeOutput.Text = "Focus → " + Unwrap(raw);
        }
        catch (Exception ex)
        {
            ProbeOutput.Text = "Focus failed: " + ex.Message;
        }
    }

    /// <summary>Verify points (a) + (b): report the focused element's value (never a credential —
    /// truncated, and the agent only ever types throwaway text) plus the page viewport metrics.</summary>
    private async Task ProbeAsync()
    {
        if (_core is null)
        {
            SetStatus("Probe: no controller.");
            return;
        }

        const string js =
            "(function(){var a=document.activeElement||{};" +
            "var v=(a.value!==undefined&&a.value!==null)?String(a.value):null;if(v!==null)v=v.slice(0,120);" +
            "return JSON.stringify({tag:a.tagName||null,id:a.id||null,name:(a.getAttribute?a.getAttribute('name'):null)," +
            "type:a.type||null,value:v,innerWidth:window.innerWidth,innerHeight:window.innerHeight," +
            "dpr:window.devicePixelRatio,url:location.href});})()";
        try
        {
            string raw = await _core.ExecuteScriptAsync(js);
            ProbeOutput.Text = "Probe → " + Unwrap(raw);
        }
        catch (Exception ex)
        {
            ProbeOutput.Text = "Probe failed: " + ex.Message;
        }
    }

    /// <summary>Verify point (e): list cookie NAMES + flags for the current URL. NEVER values
    /// (agent-safety) — but IsHttpOnly visibility is the whole point, since the login capture rests
    /// on the CookieManager returning HttpOnly cookies that document.cookie can't see.</summary>
    private async Task CookiesAsync()
    {
        if (_core is null)
        {
            SetStatus("Cookies: no controller.");
            return;
        }

        string url = _core.Source;
        if (string.IsNullOrEmpty(url))
        {
            url = UrlBox.Text ?? "";
        }

        try
        {
            System.Collections.Generic.IReadOnlyList<CoreWebView2Cookie> cookies =
                await _core.CookieManager.GetCookiesAsync(url);

            StringBuilder sb = new();
            sb.AppendLine($"Cookies for {url}: {cookies.Count}");
            int httpOnly = 0;
            foreach (CoreWebView2Cookie c in cookies)
            {
                if (c.IsHttpOnly)
                {
                    httpOnly++;
                }

                // NAMES + flags only — no values.
                sb.AppendLine($"  {c.Name}  httpOnly={c.IsHttpOnly} secure={c.IsSecure} domain={c.Domain}");
            }

            sb.AppendLine($"HttpOnly count: {httpOnly}");
            ProbeOutput.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            ProbeOutput.Text = "Cookies failed: " + ex.Message;
        }
    }

    /// <summary>The agent's eye inside the WebView: <see cref="CoreWebView2.CapturePreviewAsync"/>
    /// a PNG to a timestamped file and report the path on the Avalonia side.</summary>
    private async Task CaptureAsync()
    {
        if (_core is null)
        {
            SetStatus("Capture: no controller.");
            return;
        }

        try
        {
            Directory.CreateDirectory(CaptureDir);
            string path = Path.Combine(CaptureDir, $"webview-capture-{DateTime.Now:HHmmss}.png");
            await using (FileStream fs = File.Create(path))
            {
                await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            }

            ProbeOutput.Text = "Capture → " + path;
            SetStatus("Captured " + path);
        }
        catch (Exception ex)
        {
            ProbeOutput.Text = "Capture failed: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Decodes the JSON-string ExecuteScriptAsync returns (e.g. <c>"\"…\""</c>) into a
    /// plain string; passes anything else through unchanged.</summary>
    private static string Unwrap(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            return raw ?? "null";
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }
}
