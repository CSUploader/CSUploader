// <copyright file="WpfClipboardService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;

namespace CSUploader.Services;

/// <summary>
/// WPF implementation of <see cref="IClipboardService"/>. Wraps <see cref="Clipboard"/>, whose
/// operations complete synchronously on the UI thread — the returned tasks are already completed.
/// Contention exceptions are propagated (callers swallow them exactly as they did the sync calls).
/// </summary>
public sealed class WpfClipboardService : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        Clipboard.Clear();
        return Task.CompletedTask;
    }
}
