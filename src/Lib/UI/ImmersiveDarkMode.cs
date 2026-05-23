// <copyright file="ImmersiveDarkMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CSUploader.Lib.UI;

/// <summary>
/// Toggles the Windows "immersive dark mode" attribute on top-level WPF windows so the
/// non-client title bar matches the in-app dark theme. Falls back silently on Windows
/// versions that don't support it (the call returns a non-success HRESULT but no harm).
/// </summary>
public static class ImmersiveDarkMode
{
    // Windows 10 build 19041 (20H1) and later use attribute 20.
    private const int DwmwaUseImmersiveDarkMode = 20;

    // On Windows 10 builds 18985..19040 (1909/early 20H1 insider) the attribute id was 19.
    // Newer DWMs ignore writes to this id, older ones fail attr 20 silently — so we try
    // attr 20 first and fall back to 19. Cheap to call both; either succeeds or both
    // no-op (pre-1809, VMs running 1803 etc.) and the title bar stays the OS default.
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>
    /// Current dark-mode preference. Updated via <see cref="SetIsDark"/>; the
    /// global Window.Loaded handler reads this when newly opened windows appear.
    /// </summary>
    public static bool IsDark { get; private set; }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const uint WmNcActivate = 0x0086;

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

            // Always write to both attribute ids. The HRESULT-based dispatch we used
            // before was unreliable: on some VMs running Win10 1909, DWM accepts attr
            // 20 with HRESULT 0 but does nothing visible — so we never fell through to
            // attr 19. Newer DWMs honour attr 20 and reject 19 with E_INVALIDARG (safe
            // to ignore); older DWMs do the opposite. Either way the title bar lands
            // on the right value, and on truly ancient Windows both calls just no-op.
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));

            // DWM only re-evaluates the title-bar style on the next non-client repaint.
            // SWP_FRAMECHANGED works on Win11/Win10 21H2+, but on the older Win10 DWMs
            // we still see (e.g. 1909 in Hyper-V) it stays in the cached "light" frame
            // until the window itself loses and regains NC-active state. Toggling
            // WM_NCACTIVATE twice (deactivate → reactivate) reproduces what a real
            // minimize/restore does and forces DWM to re-query the immersive attribute.
            // The double-send is intentional: a single WM_NCACTIVATE(1) on an already-
            // active window is a no-op on some DWMs.
            //
            // The bounce is scheduled at ContextIdle so the OS-driven WM_ACTIVATE /
            // WM_NCACTIVATE that ShowDialog generates for the new modal window lands
            // first. Doing it synchronously here works for the main window (already
            // active when SetIsDark runs) but loses the race against the modal
            // activation sequence for child dialogs (HttpDetailsWindow on Win10),
            // leaving them painted in the OS-default light chrome on first open.
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SwpFrameChanged | SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate);
                    _ = SendMessage(hwnd, WmNcActivate, IntPtr.Zero, new IntPtr(-1));
                    _ = SendMessage(hwnd, WmNcActivate, new IntPtr(1), new IntPtr(-1));
                }));
        }
        catch
        {
            // Best-effort: pre-Windows 10 will return E_INVALIDARG; nothing we can do
            // and the title bar just stays the OS default. Don't crash on it.
        }
    }
}
