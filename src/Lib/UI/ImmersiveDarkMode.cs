// <copyright file="ImmersiveDarkMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CSUploader.Lib.UI;

/// <summary>
/// Toggles the Windows "immersive dark mode" attribute on top-level WPF windows so the
/// non-client title bar matches the in-app dark theme. Falls back silently on Windows
/// versions that don't support it (the call returns a non-success HRESULT but no harm).
/// </summary>
public static class ImmersiveDarkMode
{
    // Windows 10 build 19041 (20H1) onward; some pre-1809 docs reference attribute 19.
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>
    /// Current dark-mode preference. Updated via <see cref="SetIsDark"/>; the
    /// global Window.Loaded handler reads this when newly opened windows appear.
    /// </summary>
    public static bool IsDark { get; private set; }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// Registers a class-level handler for <see cref="FrameworkElement.LoadedEvent"/> so
    /// every Window (existing and future) picks up the current theme without each window
    /// having to opt in. Idempotent.
    /// </summary>
    public static void RegisterGlobalHandler()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                {
                    Apply(window, IsDark);
                }
            }));
    }

    /// <summary>
    /// Updates the cached preference and reapplies the title-bar style to every
    /// currently open window. Called from MainViewModel's theme-change handler.
    /// </summary>
    public static void SetIsDark(bool dark)
    {
        IsDark = dark;
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            Apply(window, dark);
        }
    }

    /// <summary>
    /// Applies dark/light immersive title bar to a single window. Safe to call before
    /// the window's HWND exists — we ensure it via WindowInteropHelper.
    /// </summary>
    public static void Apply(Window window, bool dark)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // Best-effort: pre-Windows 10 will return E_INVALIDARG; nothing we can do
            // and the title bar just stays the OS default. Don't crash on it.
        }
    }
}
