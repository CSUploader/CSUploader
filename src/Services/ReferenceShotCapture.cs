// <copyright file="ReferenceShotCapture.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSUploader.ViewModels;
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
}
#endif
