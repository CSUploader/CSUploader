// <copyright file="CefBootstrap.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if !WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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

        // libcef.so ships in the CefGlueBrowserProcess SUBFOLDER (the cef.redist layout copies the whole CEF
        // payload there, next to the subprocess host), which the OS loader does NOT search — it only probes the
        // app root and the .NET runtime dir. Every P/Invoke in Xilium.CefGlue.dll declares [DllImport("libcef")],
        // and the FIRST such call in the sign-in path (CefRequestContext.CreateContext) can fire before CefGlue's
        // own internal libcef preload runs, so without help it throws "Unable to load shared library 'libcef'".
        // Register a resolver on the CefGlue interop assembly that loads libcef from the subfolder — this makes a
        // shipped build work with no LD_LIBRARY_PATH, independent of call ordering (the spike relies on CefGlue's
        // implicit preload; this pins it deterministically).
        RegisterLibcefResolver();

        Directory.CreateDirectory(RootCachePath);

        // Use offscreen rendering (OSR). CefGlue.Avalonia's AvaloniaCefBrowser routes on this flag
        // (BaseCefBrowser ctor): true selects the OSR adapter (CommonOffscreenBrowserAdapter, SetAsWindowless) it
        // is DESIGNED for on Linux; false selects the WINDOWED adapter (CommonBrowserAdapter, SetAsChild) that
        // reparents a native CEF X11 child window into Avalonia's NativeControlHost. The windowed path is FRAGILE
        // on Linux — its GPU process fails init and it emits an X protocol error on window teardown
        // ("X error received. DestroyWindowRequest") that an X server can treat as fatal — whereas OSR runs clean.
        // The flag also gates CefGlue's own GPU switches: BrowserCefApp.OnBeforeCommandLineProcessing appends
        // disable-gpu + disable-gpu-compositing + enable-begin-frame-scheduling ONLY in OSR mode, so OSR both
        // selects the robust adapter AND disables the GPU up front (WebGL still renders via the software GL path —
        // no extra SwiftShader flags needed; verified under WSLg software-GL). Matches the CefGlue.Demo.Avalonia
        // canonical setup. The shipped CEF payload carries no chrome-sandbox binary, so no-sandbox stays (else the
        // render subprocess can't launch on Linux).
        CefRuntimeLoader.Initialize(
            new CefSettings
            {
                RootCachePath = RootCachePath,
                WindowlessRenderingEnabled = true,
            },
            flags: new[] { new KeyValuePair<string, string>("no-sandbox", string.Empty) });

        // NO ProcessExit → CefRuntime.Shutdown() here: CefGlue's own CefRuntimeLoader.InternalInitialize already
        // registers one when the first browser is created (verified in the 120.6099.211 assembly). CefRuntime.Shutdown
        // does not reset its initialized flag, so a second handler would call the native libcef.shutdown() TWICE at
        // exit — the double-teardown UB class we hit elsewhere. If no sign-in ever runs, CEF is never initialized and
        // there is nothing to shut down.
    }

    // Resolves the "libcef" import to <app>/CefGlueBrowserProcess/libcef.so (where the cef.redist payload lives)
    // when that file exists; otherwise falls back to the default loader (IntPtr.Zero). Registered on the assembly
    // that owns the [DllImport("libcef")] declarations (Xilium.CefGlue.dll, the same one that declares CefRuntime)
    // so the resolver fires for every libcef P/Invoke regardless of which CefGlue call triggers it first. Only ever
    // reached on non-Windows (Windows uses WebView2). SetDllImportResolver is once-per-assembly; Initialize() is
    // idempotent, so this registers exactly once.
    private static void RegisterLibcefResolver()
    {
        string libcefPath = Path.Combine(AppContext.BaseDirectory, "CefGlueBrowserProcess", "libcef.so");
        if (!File.Exists(libcefPath))
        {
            return; // nothing to redirect (e.g. a flat layout); leave default resolution untouched
        }

        NativeLibrary.SetDllImportResolver(typeof(CefRuntime).Assembly, (libraryName, _, _) =>
            libraryName == "libcef" && NativeLibrary.TryLoad(libcefPath, out nint handle)
                ? handle
                : IntPtr.Zero);
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
}
#endif
