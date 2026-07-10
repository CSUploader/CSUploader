// <copyright file="WpfUiShell.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;

namespace CSUploader.Services;

/// <summary>
/// WPF implementation of <see cref="IUiShell"/>. Wraps <see cref="Application.Current"/>'s main
/// window and shutdown. Null-tolerant for headless tests: both members no-op when no
/// <see cref="Application"/> is running.
/// </summary>
public sealed class WpfUiShell : IUiShell
{
    public void ActivateMainWindow()
    {
        if (Application.Current?.MainWindow is not { } window)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    public void Shutdown() => Application.Current?.Shutdown();
}
