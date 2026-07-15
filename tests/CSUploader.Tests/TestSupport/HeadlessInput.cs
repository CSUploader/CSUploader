// <copyright file="HeadlessInput.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Shared headless input drivers for the window behavior tests (MessageBoxWindowTests, SimpleDialogTests).
/// Hoisted from those classes so the click/key idioms live in one place: raising a button's routed
/// <c>Click</c> directly (no reliance on hit-testing a small button in the headless surface) and pressing a
/// logical key through the non-obsolete <c>KeyPress</c> overload.
/// </summary>
internal static class HeadlessInput
{
    /// <summary>
    /// Raises the <see cref="Button.ClickEvent"/> the real pointer/keyboard click raises, invoking the
    /// XAML-wired handler deterministically without hit-testing the button.
    /// </summary>
    internal static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// The non-obsolete <c>KeyPress</c> overload (logical key + physical key). Enter/Esc carry the logical
    /// <see cref="Key"/> the <c>IsDefault</c>/<c>IsCancel</c> button handlers listen for; the physical key +
    /// null symbol satisfy the API. Those keys route to the button's Click but do NOT auto-close the window
    /// (Reality-check #1) — the explicit Close handler is what dismisses it.
    /// </summary>
    internal static void Press(Window window, Key key, PhysicalKey physical)
        => window.KeyPress(key, RawInputModifiers.None, physical, null);
}
