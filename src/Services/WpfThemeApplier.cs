// <copyright file="WpfThemeApplier.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CSUploader.Lib;

namespace CSUploader.Services;

/// <summary>
/// WPF implementation of <see cref="IThemeApplier"/>. Writes into
/// <see cref="Application.Current"/>'s live resource dictionary. Null-tolerant for headless
/// tests: both members no-op when no <see cref="Application"/> is running, exactly as the
/// former in-ViewModel code did.
/// </summary>
public sealed class WpfThemeApplier(IAppLogger logger) : IThemeApplier
{
    private readonly IAppLogger _logger = logger;

    public void ApplyGridFont(string family, double size)
    {
        Application app = Application.Current;
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
            _logger.Log(this, LogType.Error, $"Failed to apply grid font: {ex.Message}");
        }
    }

    public void ApplyTheme(bool isDark)
    {
        Application app = Application.Current;
        if (app == null)
        {
            return;
        }

        Collection<ResourceDictionary> mergedDicts = app.Resources.MergedDictionaries;

        // Find and remove the current theme dictionary.
        ResourceDictionary? existingTheme = null;
        foreach (ResourceDictionary? dict in mergedDicts)
        {
            if (dict.Source != null &&
                (dict.Source.OriginalString.Contains("Theme.Light", StringComparison.Ordinal) ||
                 dict.Source.OriginalString.Contains("Theme.Dark", StringComparison.Ordinal)))
            {
                existingTheme = dict;
                break;
            }
        }

        if (existingTheme != null)
        {
            mergedDicts.Remove(existingTheme);
        }

        // Add the new theme dictionary.
        string themeFile = isDark ? "Resources/Theme.Dark.xaml" : "Resources/Theme.Light.xaml";
        var newTheme = new ResourceDictionary
        {
            Source = new Uri(themeFile, UriKind.Relative),
        };
        mergedDicts.Add(newTheme);

        // Re-apply the immersive dark title bar to every currently open window (newly
        // opened windows pick this up via the global Window.Loaded handler). Runs after
        // the dictionary swap, mirroring the former MainViewModel ordering.
        Lib.UI.ImmersiveDarkMode.SetIsDark(isDark);
    }
}
