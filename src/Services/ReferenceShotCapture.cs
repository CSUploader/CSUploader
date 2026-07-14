// <copyright file="ReferenceShotCapture.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if DEBUG
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Services;

/// <summary>
/// DEBUG-only reference-shot capture (design §MCP dev loop): renders the main window's
/// client area per tab, light + dark, as 96-DPI render-tree PNGs under the shots
/// convention (&lt;view&gt;-&lt;light|dark&gt;-wpf.png), then shuts the app down.
/// RenderTargetBitmap, deliberately NOT PrintWindow — PrintWindow returns black frames
/// without PW_RENDERFULLCONTENT and captures chrome + physical pixels.
/// </summary>
public sealed class ReferenceShotCapture(IServiceProvider services)
{
    private static readonly string[] TabNames = ["uploads", "uploaded", "settings", "logs"];

    public async Task RunAndShutdownAsync(Window window, string dir)
    {
        // Task 3 sub-modes. App.xaml.cs is off-limits this phase (the one-WPF-file rule — only
        // ReferenceShotCapture.cs may change), and its dispatch only distinguishes --dialogs, so
        // the --wizard / --settings selection is read here from the process command line instead of
        // adding a branch to the App startup. Bare --shots still captures the main-window tab set
        // (incl. the queue-row mainwindow-uploads re-capture).
        string[] argv = Environment.GetCommandLineArgs();
        if (Array.IndexOf(argv, "--wizard") >= 0)
        {
            await RunWizardShotsAndShutdownAsync(window, dir);
            return;
        }

        if (Array.IndexOf(argv, "--settings") >= 0)
        {
            await RunSettingsShotsAndShutdownAsync(window, dir);
            return;
        }

        if (Array.IndexOf(argv, "--toast") >= 0)
        {
            await RunToastShotsAndShutdownAsync(dir);
            return;
        }

        Directory.CreateDirectory(dir);

        // Pin the logical size (screenshot normalization, design §MCP dev loop) — matches
        // the Avalonia shell's 1024x800 so paired shots line up.
        window.Width = 1024;
        window.Height = 800;

        // MainWindow_Loaded runs MainViewModel.InitializeAsync fire-and-forget; there is no
        // completion signal on the VM (verify at implementation — if one exists, await it
        // instead). Settle-delay is acceptable for a dev capture tool; bump it if a seeded
        // grid ever captures half-hydrated.
        await Task.Delay(2500);

        IThemeApplier theme = services.GetRequiredService<IThemeApplier>();
        MainViewModel vm = services.GetRequiredService<MainViewModel>();

        foreach (bool dark in (bool[])[false, true])
        {
            theme.ApplyTheme(dark);
            for (int i = 0; i < TabNames.Length; i++)
            {
                vm.SelectedTabIndex = i;
                await WaitForRenderAsync(window);
                CaptureWindow(window, Path.Combine(dir, $"mainwindow-{TabNames[i]}-{(dark ? "dark" : "light")}-wpf.png"));
            }
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// DEBUG-only dialog reference-shot mode (<c>--shots --dialogs</c>): opens each of the ten
    /// simple dialogs with synthesized args, light + dark, captures its client area under the
    /// shots convention (&lt;view&gt;-&lt;light|dark&gt;-wpf.png), then shuts the app down. These
    /// are the WPF reference cells the Avalonia head's dialog ports arbitrate against in the phase
    /// contact sheet. Every driver fabricates its own data in-method — no driver reads real
    /// account state. Non-modal <see cref="Window.Show"/> (not ShowDialog): nothing pumps a result
    /// here, we only need pixels.
    /// </summary>
    public async Task RunDialogsAndShutdownAsync(Window mainWindow, string dir)
    {
        Directory.CreateDirectory(dir);

        // Normalize the shell the owner-resolving dialogs parent to (Confirmation/CloseAction pick
        // the active window in their ctor); the dialogs themselves SizeToContent.
        mainWindow.Width = 1024;
        mainWindow.Height = 800;

        // Same settle preamble as RunAndShutdownAsync — let MainViewModel.InitializeAsync hydrate.
        await Task.Delay(2500);

        IThemeApplier theme = services.GetRequiredService<IThemeApplier>();

        foreach (bool dark in (bool[])[false, true])
        {
            // App-level dictionary swap: dialogs opened after this pick up the theme, and their
            // DynamicResource brushes re-color live (WpfThemeApplier.ApplyTheme).
            theme.ApplyTheme(dark);
            string suffix = dark ? "dark" : "light";
            foreach ((string name, Func<Window> factory) in DialogFactories)
            {
                Window dialog = factory();
                dialog.Show();
                await WaitForRenderAsync(dialog);
                CaptureWindow(dialog, Path.Combine(dir, $"{name}-{suffix}-wpf.png"));
                dialog.Close();
            }
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// DEBUG-only wizard reference-shot mode (<c>--shots --wizard</c>): builds a WPF
    /// <see cref="UploadWizardWindow"/> pointed at a throwaway temp folder in Directory mode and
    /// captures each of its four steps, light + dark, under the shots convention
    /// (<c>wizard-step0..3-&lt;light|dark&gt;-wpf.png</c>). These are the WPF reference cells the
    /// Avalonia wizard port (Tasks 7-9) arbitrates against. Drives <c>GoNextCommand</c> to walk the
    /// steps; ticks the seeded Catbox account so the Summary is populated WITHOUT a network round-trip
    /// (Catbox is not <c>IStorageRefreshablePipeline</c>, so entering the Summary fires no storage
    /// refresh) and NEVER drives step 3's final Add (which would start a real upload — agent-safety).
    /// </summary>
    public async Task RunWizardShotsAndShutdownAsync(Window mainWindow, string dir)
    {
        Directory.CreateDirectory(dir);
        mainWindow.Width = 1024;
        mainWindow.Height = 800;

        // Let MainViewModel.InitializeAsync hydrate the seeded accounts the hoster step reads.
        await Task.Delay(2500);

        // A throwaway source folder with a few fake files so Directory mode enumerates a real list.
        string sourceDir = Path.Combine(Path.GetTempPath(), "csuploader-wizard-shots");
        Directory.CreateDirectory(sourceDir);
        (string Name, int Kib)[] fakeFiles =
        [
            ("holiday_clip.mkv", 4096),
            ("readme.txt", 3),
            ("bundle.zip", 1024),
        ];
        foreach ((string name, int kib) in fakeFiles)
        {
            string p = Path.Combine(sourceDir, name);
            if (!File.Exists(p))
            {
                File.WriteAllBytes(p, new byte[kib * 1024]);
            }
        }

        IThemeApplier theme = services.GetRequiredService<IThemeApplier>();
        MainViewModel main = services.GetRequiredService<MainViewModel>();

        foreach (bool dark in (bool[])[false, true])
        {
            theme.ApplyTheme(dark);
            string suffix = dark ? "dark" : "light";

            // Build AFTER the theme swap so the window's DynamicResource brushes pick it up.
            UploadWizardWindow wizard = new(main.UploadsViewModel);
            var vm = (UploadWizardViewModel)wizard.DataContext!;

            // Step 0 — source + files. Set the title BEFORE DirectoryPath so LoadFiles keeps it.
            vm.Mode = UploadWizardMode.Directory;
            vm.PackageTitle = "Holiday photos & clips";
            vm.DirectoryPath = sourceDir; // OnDirectoryPathChanged → LoadFiles (every file IsSelected=true)
            wizard.Show();
            await WaitForRenderAsync(wizard);
            CaptureWindow(wizard, Path.Combine(dir, $"wizard-step0-{suffix}-wpf.png"));

            // Step 1 — hosters. GoNext validates step 0, then loads FileHosters from the seeded accounts.
            await vm.GoNextCommand.ExecuteAsync(null);
            FileHosterSelectionViewModel? catbox = vm.FileHosters.FirstOrDefault(
                h => string.Equals(h.FileHosterName, "Catbox", StringComparison.Ordinal) && h.HasAccounts);
            if (catbox is not null)
            {
                catbox.Use = true; // SelectedAccount already defaults to the seeded Catbox account
            }

            await WaitForRenderAsync(wizard);
            CaptureWindow(wizard, Path.Combine(dir, $"wizard-step1-{suffix}-wpf.png"));

            // Step 2 — Summary (the custom Expander; the visually critical pair). No network: Catbox
            // is not storage-refreshable, so RecomputeSummary's storage refresh is an empty no-op.
            await vm.GoNextCommand.ExecuteAsync(null);
            await WaitForRenderAsync(wizard);
            CaptureWindow(wizard, Path.Combine(dir, $"wizard-step2-{suffix}-wpf.png"));

            // Step 3 — start mode. Flip to Scheduled so the DatePicker (the DateTimeOffset? port) shows.
            await vm.GoNextCommand.ExecuteAsync(null);
            vm.StartMode = UploadStartMode.Scheduled;
            await WaitForRenderAsync(wizard);
            CaptureWindow(wizard, Path.Combine(dir, $"wizard-step3-{suffix}-wpf.png"));

            wizard.Close();
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// DEBUG-only settings reference-shot mode (<c>--shots --settings</c>): hosts a standalone WPF
    /// <see cref="SettingsView"/> bound to the seeded <see cref="SettingsViewModel"/> inside a shim
    /// window whose DataContext is the <see cref="MainViewModel"/> (so the Connection panel's
    /// <c>RelativeSource AncestorType=Window</c> → <c>ConnectionManagerViewModel</c> lookup resolves),
    /// capturing once per category (<c>SelectedCategoryIndex</c> 0-3) as
    /// <c>settings-general/upload/connection/accounts-&lt;light|dark&gt;-wpf.png</c> — the WPF reference
    /// cells the Avalonia SettingsView port (Tasks 4-6) arbitrates against.
    /// </summary>
    public async Task RunSettingsShotsAndShutdownAsync(Window mainWindow, string dir)
    {
        Directory.CreateDirectory(dir);
        mainWindow.Width = 1024;
        mainWindow.Height = 800;

        // Let MainViewModel.InitializeAsync hydrate the accounts + proxies the panels read.
        await Task.Delay(2500);

        IThemeApplier theme = services.GetRequiredService<IThemeApplier>();
        MainViewModel main = services.GetRequiredService<MainViewModel>();

        string[] categoryNames = ["general", "upload", "connection", "accounts"];

        foreach (bool dark in (bool[])[false, true])
        {
            theme.ApplyTheme(dark);
            string suffix = dark ? "dark" : "light";

            // A fresh SettingsView per theme so its DynamicResource brushes bind after the swap. The
            // shim window carries the MainViewModel so the Connection panel's Window-ancestor lookup
            // resolves ConnectionManagerViewModel — mirrors MainWindow hosting the view in its tab.
            SettingsView view = new() { DataContext = main.SettingsViewModel };
            Window shim = new()
            {
                Width = 1024,
                Height = 800,
                Content = view,
                DataContext = main,
            };
            shim.Show();

            for (int category = 0; category < categoryNames.Length; category++)
            {
                main.SettingsViewModel.SelectedCategoryIndex = category;
                await WaitForRenderAsync(shim);
                CaptureWindow(shim, Path.Combine(dir, $"settings-{categoryNames[category]}-{suffix}-wpf.png"));
            }

            shim.Close();
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// DEBUG-only toast reference-shot mode (<c>--shots --toast</c>): shows the WPF ToastWindow with a
    /// synthesized ToastViewModel, light + dark, and captures its card under the shots convention
    /// (toast-light-wpf.png / toast-dark-wpf.png) — the WPF reference cell the Avalonia toast port (Task 2)
    /// arbitrates against. No network, no upload. NOTE: CaptureWindow renders root.ActualWidth/Height (the
    /// toast Border, not the window), so the WPF cell CLIPS the DropShadowEffect and looks tighter/shadowless;
    /// the Avalonia side is a full-window bridge screenshot with the BoxShadow visible. That framing
    /// difference is expected (Task 8 arbitration), not a regression.
    /// </summary>
    public async Task RunToastShotsAndShutdownAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(1500); // let the app settle (no MainViewModel hydration needed for a toast)

        IThemeApplier theme = services.GetRequiredService<IThemeApplier>();

        foreach (bool dark in (bool[])[false, true])
        {
            theme.ApplyTheme(dark);
            string suffix = dark ? "dark" : "light";

            var vm = new ViewModels.ToastViewModel(
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }),
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }))
            {
                Title = Localizer.Instance["Toast_FileCompleted_Title"],
                Message = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Toast_FileCompleted_Body"], "holiday_clip.mkv"),
                IconKey = "StatusSuccessImage",
            };
            ToastWindow toast = new(vm) { Left = 200, Top = 200 };
            toast.Show();
            await WaitForRenderAsync(toast);
            CaptureWindow(toast, Path.Combine(dir, $"toast-{suffix}-wpf.png"));
            toast.Close();
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// Captures one window's client area to a PNG. Public and static on purpose: Phases 4-6
    /// reuse it for dialog reference shots (open the dialog, call this, close).
    /// </summary>
    public static void CaptureWindow(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        int w = (int)Math.Ceiling(root.ActualWidth);
        int h = (int)Math.Ceiling(root.ActualHeight);

        // Draw the window background first: rendering only the content visual misses the
        // Window's SurfaceBrush fill (set by the implicit Window style, Tokens.xaml:773-777).
        DrawingVisual dv = new();
        using (DrawingContext ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
            ctx.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }

        RenderTargetBitmap rtb = new(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    private static async Task WaitForRenderAsync(Window window)
    {
        // Two settle passes: tab-content template realization at ContextIdle + a render tick.
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(150);
    }

    // One local factory per Phase 4 view name (Task 1 synthesized-args table). All data is fake;
    // the progress/updateprogress factories set named elements post-construction to reach the
    // allowCancel / downloading look, so those two are blocks rather than expressions.
    private static IEnumerable<(string Name, Func<Window> Factory)> DialogFactories =>
    [
        ("confirmation", static () => new ConfirmationDialog(
            "Delete 3 selected packages?\nThis cannot be undone.",
            Localizer.Instance["Confirmation_WindowTitle"])),
        ("closeaction", static () => new CloseActionDialog()),
        ("speedlimit", static () => new SpeedLimitDialog(512)),
        ("proxytext-edit", static () => new ProxyTextDialog(
            "Import proxies",
            "One proxy per line, host:port[:user:pass].",
            "127.0.0.1:8080\n10.0.0.1:1080:user:pass",
            readOnly: false)),
        ("proxytext-export", static () => new ProxyTextDialog(
            "Import proxies",
            "One proxy per line, host:port[:user:pass].",
            "127.0.0.1:8080\n10.0.0.1:1080:user:pass",
            readOnly: true)),
        ("errordetails", static () => new ErrorDetailsWindow(SynthErrorDetail)),
        ("progress", static () =>
        {
            ProgressWindow w = new();
            w.LabelText.Text = "Uploading movie.mkv to 4 hosters"
                + Environment.NewLine + Localizer.Instance["Progress_LabelSuffix"];
            w.CancelButton.Visibility = Visibility.Visible;
            return w;
        }),
        ("updateprogress", static () =>
        {
            UpdateProgressWindow w = new();
            w.SetStatus(string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["UpdateProgress_StatusDownloading_Format"],
                "1.2.3"));
            w.SetProgress(42);
            return w;
        }),
        ("about", static () => new AboutWindow()),
        ("logdetails", static () => new LogDetailsWindow(new LogEntryViewModel(SynthLogEvent()))),
        ("httpdetails", static () => new HttpDetailsWindow(SynthTransaction())),
        ("editaccount-classic", static () => new EditAccountWindow(
            new FileHosterLoginDto
            {
                Id = 1, // edit mode → locked-hoster border (EditAccountWindow.xaml.cs:109-117)
                FileHosterName = "Rapidgator",
                Username = "fake_rg_user",
                Password = "not-a-real-password",
                AccountType = AccountType.Premium,
            },
            ["Rapidgator", "KatFile", "Isracloud"])),
        ("editaccount-apikey", static () => new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free, ApiKey = "fake-api-key-0123456789abcdef" },
            ["Rapidgator", "KatFile", "Isracloud"])),
        ("editaccount-cookie", static () => new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "Isracloud", AccountType = AccountType.Free },
            ["Rapidgator", "KatFile", "Isracloud"])),
        ("editaccount-error", static () =>
        {
            EditAccountWindow w = new(
                new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free },
                ["Rapidgator", "KatFile", "Isracloud"]);
            // The error state ShowSignInError produces (EditAccountWindow.xaml.cs:227-237), poked
            // via the internal x:Name fields — the same post-construction technique as `progress`.
            w.SignInStatus.Visibility = Visibility.Collapsed;
            w.SignInErrorPanel.Visibility = Visibility.Visible;
            w.SignInErrorText.Text = string.Format(
                CultureInfo.CurrentCulture, "{0}: {1}",
                Localizer.Instance["Common_Error"], "Sign-in failed: invalid credentials");
            return w;
        }),
        ("editproxy", static () => new EditProxyWindow(
            new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080, Username = "fake_proxy_user", Password = "not-a-real-password", Enabled = true })),
        ("editproxy-tested", static () =>
        {
            EditProxyWindow w = new(
                new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080, Enabled = true });
            // The post-Test look (EditProxyWindow.xaml.cs:87-89, :108): OK status line + Details button.
            w.TestStatusText.Text = string.Format(
                CultureInfo.CurrentCulture, Localizer.Instance["EditProxy_Status_OkLatencyIp_Format"], 142, "203.0.113.7");
            w.TestStatusText.Visibility = Visibility.Visible;
            w.TestDetailsButton.Visibility = Visibility.Visible;
            return w;
        }),
    ];

    // A verbose sign-in failure — the human summary plus a ~600-char HTML error page, the kind of
    // payload ErrorDetailsWindow exists to show (mirrors an XFileSharing 403 body).
    private const string SynthErrorDetail =
        "Sign-in failed: invalid credentials\n\n"
        + "<html><head><title>403 Forbidden</title></head><body>\n"
        + "<h1>Access Denied</h1>\n"
        + "<p>Your login request could not be processed. The username or password you entered does "
        + "not match our records, or the account has been temporarily locked after too many failed "
        + "attempts.</p>\n"
        + "<p>Reference ID: 7f3a9c21-4b8e-4d0a-9e11-2c6b5a1f0d43</p>\n"
        + "<p>If you believe this is an error, contact support and quote the reference above. "
        + "Automated retries will not succeed until the lock expires (approximately 15 minutes).</p>\n"
        + "<hr><address>nginx/1.24.0 at api.example-hoster.com Port 443</address>\n"
        + "</body></html>";

    // Mirrors Logger.Log's LogEvent shape (bare filename, method name, managed thread id, a
    // multi-line message) so the LogDetails fields render as they do in production.
    private static LogEvent SynthLogEvent() => new()
    {
        LogType = LogType.Error,
        DateTime = new DateTime(2026, 7, 12, 14, 30, 45, 123),
        ThreadId = 7,
        Filename = "FileHosterClient.cs",
        Function = "UploadAsync",
        LineNumber = 214,
        Message = "Upload failed after 3 retries.\n"
            + "HTTP 503 Service Unavailable\n"
            + "The origin server is temporarily unable to service the request.",
    };

    // A complete POST transaction with JSON bodies (so the Body-JSON sub-tabs pretty-print) and
    // ResponseBodyBytes (so the Hex sub-tab renders). Duration is computed from Start/EndTime.
    private static HttpTransaction SynthTransaction()
    {
        DateTime start = new(2026, 7, 12, 14, 30, 45);
        return new HttpTransaction
        {
            Method = "POST",
            Url = "https://api.example-hoster.com/v1/upload",
            Proxy = "http://127.0.0.1:8080",
            StatusCode = 200,
            StatusReason = "OK",
            StartTime = start,
            EndTime = start.AddMilliseconds(842),
            RequestHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"],
                ["Authorization"] = ["Bearer synthesized-session-token"],
                ["User-Agent"] = ["CSUploader/0.0.6"],
            },
            RequestBody = "{\"name\":\"movie.mkv\",\"size\":5242880,\"folderId\":0}",
            ResponseHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"],
                ["Server"] = ["nginx"],
            },
            ResponseBody = "{\"status\":\"ok\",\"fileId\":\"abc123\",\"url\":\"https://example-hoster.com/f/abc123\"}",
            ResponseBodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"fileId\":\"abc123\"}"),
        };
    }
}
#endif
