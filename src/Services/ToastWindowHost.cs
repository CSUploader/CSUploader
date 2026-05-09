// <copyright file="ToastWindowHost.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Adapts <see cref="ToastWindow"/> to the <see cref="IToastHost"/> contract so the
/// service can drive it without a direct WPF dependency.
/// </summary>
internal sealed class ToastWindowHost : IToastHost
{
    private readonly ToastWindow _window;

    public ToastWindowHost(ToastWindow window)
    {
        _window = window;
        _window.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
    }

    public double Height => _window.Height;

    public double Top
    {
        get => _window.Top;
        set => _window.Top = value;
    }

    public double Left
    {
        get => _window.Left;
        set => _window.Left = value;
    }

    public event EventHandler? Closed;

    public void Show() => _window.Show();

    public void Close() => _window.Close();
}
