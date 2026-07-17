// <copyright file="AvaloniaToastHost.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CSUploader.Lib.UI;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Adapts ToastWindow to IToastHost. Top/Left are DIPs (the service computes the stack in DIPs); this host
/// converts to physical Window.Position via the PRIMARY screen's Scaling — the same primary screen the DI
/// workAreaProvider reads, so the two agree. Primary-monitor-only, matching the WPF head's SystemParameters.WorkArea.
/// </summary>
internal sealed class AvaloniaToastHost : IToastHost
{
    private readonly ToastWindow _window;

    public AvaloniaToastHost(ToastWindow window)
    {
        _window = window;
        _window.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
    }

    // ToastWindow.Height is the fixed DIP height (Avalonia Height is logical/DIP) — what the service stacks from.
    public double Height => _window.Height;

    public double Top
    {
        get;
        set
        {
            field = value;
            ApplyPosition();
        }
    }

    public double Left
    {
        get;
        set
        {
            field = value;
            ApplyPosition();
        }
    }

    public event EventHandler? Closed;

    public void Show() => _window.Show();

    public void Close() => _window.Close();

    private void ApplyPosition()
    {
        double scaling = ResolvePrimaryScaling();
        _window.Position = ToastPlacement.DipToPhysical(Left, Top, scaling);
    }

    private static double ResolvePrimaryScaling()
    {
        Window? main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return main?.Screens?.Primary?.Scaling ?? 1.0;
    }
}
