// <copyright file="IClipboardService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Abstraction over the system clipboard. The WPF head wraps WPF's
/// <c>Clipboard</c>; the Avalonia head supplies its own. Async because
/// Avalonia's clipboard API is asynchronous — the WPF implementation completes
/// synchronously and returns a completed task.
/// </summary>
public interface IClipboardService
{
    /// <summary>Places <paramref name="text"/> on the clipboard as Unicode text.</summary>
    Task SetTextAsync(string text);

    /// <summary>Clears the clipboard contents.</summary>
    Task ClearAsync();
}
