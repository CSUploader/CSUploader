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
    /// Applies the light or dark theme to the app's live resource surface so already-open windows
    /// and controls re-render. The contract is head-agnostic; each head owns its own mechanism: the
    /// WPF head swaps a Theme.Light/Theme.Dark merged dictionary, while the Avalonia head sets
    /// <c>Application.RequestedThemeVariant</c> and lets its ThemeVariant token dictionaries follow.
    /// Handles the user toggling the theme after startup.
    /// </summary>
    void ApplyTheme(bool isDark);
}
