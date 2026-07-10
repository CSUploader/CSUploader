// <copyright file="IThemeApplier.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Applies theme-related changes to the application's live resource surface. The WPF head
/// writes into <c>Application.Current.Resources</c>; the Avalonia head supplies its own.
/// Keeps WPF resource types out of the shared Settings/Main ViewModels.
/// </summary>
public interface IThemeApplier
{
    /// <summary>
    /// Pushes the grid font family/size into the app resources so the DataGrids' DynamicResource
    /// bindings pick up the change live. Mirrors the former
    /// <c>SettingsViewModel.ApplyGridFontResources</c>: writes BOTH <c>GridFontFamily</c> and
    /// <c>GridFontSize</c>.
    /// </summary>
    void ApplyGridFont(string family, double size);

    /// <summary>
    /// Swaps the light/dark theme resource dictionary at runtime. Mirrors the former
    /// <c>MainViewModel.ApplyTheme</c>: removes the current Theme.Light/Theme.Dark merged
    /// dictionary and merges the other. (App.xaml merges Theme.Light at startup; this handles
    /// the user toggling the theme afterwards.)
    /// </summary>
    void ApplyTheme(bool isDark);
}
