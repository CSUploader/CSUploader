// <copyright file="AvaloniaImmersiveDarkMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CSUploader.Lib.UI;

/// <summary>
/// Toggles the Windows "immersive dark mode" title-bar attribute on top-level Avalonia windows so the
/// non-client title bar matches the in-app dark theme on Windows 10 (Win11 recolors it automatically from
/// the ThemeVariant). Direct port of the WPF head's ImmersiveDarkMode (rule 45): the DWM/user32 P/Invokes
/// are framework-agnostic; only the HWND acquisition (Window.TryGetPlatformHandle) and the global new-window
/// hook (Control.LoadedEvent class handler) are Avalonia-idiomatic. AvaloniaThemeApplier is the SOLE writer
/// of IsDark (design Phase 1-gate note).
/// </summary>
public static class AvaloniaImmersiveDarkMode
{
    // Windows 10 build 19041 (20H1) and later use attribute 20.
    private const int DwmwaUseImmersiveDarkMode = 20;

    // On Windows 10 builds 18985..19040 (1909/early 20H1 insider) the attribute id was 19.
    // Newer DWMs ignore writes to this id, older ones fail attr 20 silently — so we write both.
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WmNcActivate = 0x0086;

    private static bool _registered;

    /// <summary>Current dark-mode preference; updated via SetIsDark, read by the Loaded class handler.</summary>
    public static bool IsDark { get; private set; }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Registers a class handler on Control.LoadedEvent so every Window (existing and future) picks
    /// up the current theme without opting in — the Avalonia analog of WPF's EventManager.RegisterClassHandler.
    /// Idempotent.</summary>
    public static void RegisterGlobalHandler()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        Control.LoadedEvent.AddClassHandler<Window>((window, _) => Apply(window, IsDark));
    }

    /// <summary>Updates the cached preference and reapplies to every currently open window. Called from the
    /// theme-applier's theme-change path (the sole writer).</summary>
    public static void SetIsDark(bool dark)
    {
        IsDark = dark;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (Window window in desktop.Windows)
        {
            Apply(window, dark);
        }
    }

    /// <summary>Applies dark/light immersive title bar to a single window. Best-effort — no-ops (harmlessly)
    /// where the HWND is unavailable (headless) or the OS predates the attribute.</summary>
    public static void Apply(Window window, bool dark)
    {
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            {
                return;
            }

            int value = dark ? 1 : 0;

            // Write BOTH attribute ids (the WPF comment: some Win10 1909 DWMs accept 20 with HRESULT 0 but
            // do nothing, others reject it — writing 19 and 20 lands the right value on every DWM).
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));

            // Force DWM to re-query the immersive attribute on the next NC repaint (older Win10 DWMs cache the
            // frame until the window loses/regains NC-active). Scheduled at ContextIdle off the current call so
            // the OS activation sequence for a just-shown modal lands FIRST — the WPF original's exact priority
            // (its comment engineers against a child-dialog first-open flash on Win10). 11.3.18 HAS
            // DispatcherPriority.ContextIdle; Background runs sooner and would lose that race.
            Dispatcher.UIThread.Post(
                () =>
                {
                    _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SwpFrameChanged | SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate);
                    _ = SendMessage(hwnd, WmNcActivate, IntPtr.Zero, new IntPtr(-1));
                    _ = SendMessage(hwnd, WmNcActivate, new IntPtr(1), new IntPtr(-1));
                },
                DispatcherPriority.ContextIdle);
        }
        catch
        {
            // Best-effort: pre-Win10 returns E_INVALIDARG; the title bar just stays the OS default.
        }
    }
}
