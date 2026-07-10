// THROWAWAY — Phase 2 WebView2 spike; superseded by the Phase 8 login host.
using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace CSUploader.Spike;

/// <summary>
/// A <see cref="NativeControlHost"/> that owns a bare Win32 child HWND (window class "static")
/// to serve as the parent window for a WebView2 <c>CoreWebView2Controller</c>. This host only
/// manages the HWND lifecycle and surfaces it; <see cref="WebView2SpikeWindow"/> creates the
/// controller (parented to <see cref="Hwnd"/>) and keeps its bounds synced.
/// </summary>
/// <remarks>
/// Avalonia repositions the returned child HWND to overlay this control in physical pixels, so
/// the WebView2 controller only ever needs a (0,0,width,height) fill of the child's client area.
/// The child's client size is exposed via <see cref="TryGetChildClientSize"/> as the physical
/// ground truth the spike window cross-checks against DIP × RenderScaling.
/// </remarks>
internal sealed class WebView2HwndHost : NativeControlHost
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;

    /// <summary>The child HWND, or <see cref="IntPtr.Zero"/> before creation / after destroy.</summary>
    public IntPtr Hwnd { get; private set; }

    /// <summary>Raised on the UI thread once the child HWND exists, so the spike window can
    /// create the WebView2 controller parented to it.</summary>
    public event Action<IntPtr>? HwndReady;

    /// <summary>Raised on the UI thread just before the child HWND is destroyed, so the
    /// controller can be closed before its parent window disappears.</summary>
    public event Action? HwndDestroying;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        Hwnd = CreateWindowExW(
            0,
            "static",
            null,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1,
            parent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (Hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed: Win32 error {Marshal.GetLastWin32Error()}");
        }

        HwndReady?.Invoke(Hwnd);
        return new PlatformHandle(Hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        HwndDestroying?.Invoke();

        if (control.Handle != IntPtr.Zero)
        {
            DestroyWindow(control.Handle);
        }

        Hwnd = IntPtr.Zero;
    }

    /// <summary>Reads the child HWND's client rectangle (physical pixels). Returns false when
    /// the HWND does not exist or the call fails.</summary>
    public bool TryGetChildClientSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (Hwnd == IntPtr.Zero || !GetClientRect(Hwnd, out RECT r))
        {
            return false;
        }

        width = r.Right - r.Left;
        height = r.Bottom - r.Top;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
}
