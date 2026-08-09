// <copyright file="AvaloniaThemeApplier.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CSUploader.Lib;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IThemeApplier"/>. <see cref="ApplyTheme"/> sets
/// <see cref="Application.RequestedThemeVariant"/> (the ThemeVariant dictionaries in
/// ThemeBrushes.axaml follow); <see cref="ApplyGridFont"/> writes the two grid-font resources the
/// DataGrids consume via DynamicResource. SOLE writer of the theme variant — App.axaml's hardcoded
/// <c>RequestedThemeVariant="Light"</c> is the app's default, and the first call comes from
/// App.axaml.cs with the saved preference <b>before the first window is built</b>, so a dark-theme
/// shell never paints light first (design prep item 5). Also the sole writer of the
/// new-window dark-chrome preference (<see cref="Lib.UI.AvaloniaImmersiveDarkMode"/>.IsDark) — wired in
/// <see cref="ApplyTheme"/> via SetIsDark. Win11 recolors the title bar with the variant automatically;
/// the Win10 DWM P/Invoke is the fallback.
/// </summary>
public sealed class AvaloniaThemeApplier(IAppLogger logger) : IThemeApplier
{
    public void ApplyTheme(bool isDark)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        // Win11 recolors the title bar from the variant automatically; on Win10 the DWM P/Invoke is the fallback.
        // This applier is the SOLE writer of the cached new-window dark-chrome preference (design Phase 1-gate
        // note) — mirrors WpfThemeApplier.ApplyTheme -> ImmersiveDarkMode.SetIsDark.
        Lib.UI.AvaloniaImmersiveDarkMode.SetIsDark(isDark);
    }

    public void ApplyGridFont(string family, double size)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            app.Resources["GridFontFamily"] = new FontFamily(family);
            app.Resources["GridFontSize"] = size;
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to apply grid font: {ex.Message}");
        }
    }
}
