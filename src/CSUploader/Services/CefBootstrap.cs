// <copyright file="CefBootstrap.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if !WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using CSUploader.Views; // WebViewLoginProxy.SanitizeFolderName
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Shared;

namespace CSUploader.Services;

/// <summary>
/// Non-Windows CEF runtime bootstrap for the CefGlue-based interactive sign-in. CEF initializes ONCE
/// per process (via <c>Xilium.CefGlue.Common</c>), inside <c>AppBuilder.AfterSetup</c> — after Avalonia's
/// platform is detected but BEFORE the desktop lifetime starts and any <c>AvaloniaCefBrowser</c> is
/// created (the CefGlue.Demo.Avalonia canonical ordering; spike-confirmed). Windows uses WebView2 and
/// never loads this file (it is excluded from the Windows compile). CEF is shut down at process exit.
/// </summary>
internal static class CefBootstrap
{
    // Root under which each login's per-CefRequestContext CachePath lives (Task 4). Stable (not per-launch)
    // so captcha-solver trust persists across runs, mirroring the WebView2 per-hoster user-data folders.
    private static readonly string RootCachePath = Path.Combine(ResolveLocalAppData(), "CSUploader", "cef");

    // GetFolderPath(LocalApplicationData) returns an EMPTY string on Unix when ~/.local/share does not yet
    // exist (documented .NET Unix behavior) — a fresh Linux/macOS user hits this, and an empty base makes the
    // cache path relative, so Directory.CreateDirectory throws and crashes startup. Resolve robustly: the XDG
    // dir, else $HOME/.local/share, else beside the executable (the app's DB convention). Windows always
    // returns a valid LocalApplicationData, so this only changes non-Windows behavior.
    private static string ResolveLocalAppData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(appData) && Path.IsPathRooted(appData))
        {
            return appData;
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
        {
            return xdg;
        }

        string home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Path.IsPathRooted(home)
            ? Path.Combine(home, ".local", "share")
            : AppContext.BaseDirectory;
    }

    private static bool _initialized;

    /// <summary>Initializes CEF once. Idempotent. Call from <c>AppBuilder.AfterSetup</c>.</summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Directory.CreateDirectory(RootCachePath);

        // The shipped CEF payload carries no chrome-sandbox binary, so the sandbox must be disabled or the
        // render/GPU subprocess fails to launch on Linux. GPU-init failure auto-falls-back to software
        // rendering (verified in the spike), so --disable-gpu is intentionally NOT forced here — it only
        // suppresses harmless startup churn and would drop hardware acceleration on capable desktops.
        CefRuntimeLoader.Initialize(
            new CefSettings
            {
                RootCachePath = RootCachePath,
                WindowlessRenderingEnabled = false,
            },
            flags: new[] { new KeyValuePair<string, string>("no-sandbox", string.Empty) });

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    /// <summary>
    /// Returns (creating if needed) the per-hoster cache directory for a login's <see cref="CefRequestContext"/>.
    /// It is a child of <see cref="RootCachePath"/> — CEF requires a context cache path to equal or be a child
    /// of the settings root — and stable per hoster so captcha-solver trust persists across runs, mirroring the
    /// WebView2 per-hoster user-data folders. The process-wide login gate serializes logins, so two live
    /// contexts never share one cache path at once.
    /// </summary>
    internal static string LoginCachePathFor(string hosterName)
    {
        string path = Path.Combine(RootCachePath, WebViewLoginProxy.SanitizeFolderName(hosterName));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Shuts CEF down at process exit (best-effort; must run after all browsers have closed).</summary>
    public static void Shutdown()
    {
        try
        {
            CefRuntime.Shutdown();
        }
        catch
        {
            // Best-effort at exit — the process is going away regardless.
        }
    }
}
#endif
