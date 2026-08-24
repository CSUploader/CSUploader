// <copyright file="AvaloniaStartupUpdatePrompt.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Shows <see cref="UpdatePromptWindow"/> over the real main window.
/// </summary>
/// <remarks>
/// The owner is resolved at call time rather than captured, and deliberately through
/// <see cref="DialogOwnerResolver.ResolveVisibleMainOnly"/>: during startup the splash may still be
/// the lifetime's MainWindow for a moment, and a prompt owned by a window that is about to close
/// would go with it.
/// </remarks>
public sealed class AvaloniaStartupUpdatePrompt : IStartupUpdatePrompt
{
    public async Task<StartupUpdatePromptResult> ShowAsync(string newVersion, string currentVersion, bool askAtStartup)
    {
        UpdatePromptWindow window = new();
        window.SetVersions(newVersion, currentVersion, askAtStartup);

        Window? owner = DialogOwnerResolver.ResolveVisibleMainOnly();
        if (owner is null)
        {
            // No visible main window to own it. Showing it ownerless is still better than not
            // asking, and it keeps the taskbar entry so it cannot be lost behind other windows.
            window.ShowInTaskbar = true;
            window.Show();
            await WaitForCloseAsync(window);
            return window.Result;
        }

        await window.ShowDialog(owner);
        return window.Result;
    }

    private static Task WaitForCloseAsync(Window window)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        return closed.Task;
    }
}
