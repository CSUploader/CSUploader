// <copyright file="SettingsView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;

namespace CSUploader.Views;

/// <summary>
/// The Settings tab: a ListBox sidebar over four SelectedCategoryIndex-switched panels. Avalonia port of
/// the WPF <c>SettingsView</c>. Task 4 lands the shell + the General and Upload panels (bound directly to
/// <see cref="ViewModels.SettingsViewModel"/>); the Connection (proxies) and Accounts grids fill panels
/// 2/3 in Tasks 5/6, at which point their code-behind (context menus, split-button dropdowns, the
/// checkbox fan-out) arrives here. The shell itself needs no code-behind — panel switching is a pure
/// StepVisibilityConverter binding.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
