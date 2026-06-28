// <copyright file="IToastHost.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Test seam wrapping a single toast window. The production implementation forwards
/// to a <c>ToastWindow</c>; tests use an in-memory fake to exercise the stack/position
/// logic without opening real WPF windows.
/// </summary>
public interface IToastHost
{
    /// <summary>
    /// The toast's height in DIPs. Production reads this from the WPF window after layout;
    /// tests can return a fixed value.
    /// </summary>
    public double Height { get; }

    /// <summary>
    /// Top-edge position in screen coordinates. The service writes this when stacking
    /// or re-flowing toasts.
    /// </summary>
    public double Top { get; set; }

    /// <summary>
    /// Left-edge position in screen coordinates.
    /// </summary>
    public double Left { get; set; }

    /// <summary>
    /// Raised when the toast is dismissed (auto-timeout, close button, or click).
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Shows the toast non-modally.
    /// </summary>
    public void Show();

    /// <summary>
    /// Closes the toast.
    /// </summary>
    public void Close();
}
