// <copyright file="ToastViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CSUploader.ViewModels;

/// <summary>
/// View-model for a single completion-toast popup. The host window binds Title /
/// Message / IconKey for display and ActivateCommand / CloseCommand for input.
/// The toast service builds the VM (including the commands) when raising a toast.
/// </summary>
public partial class ToastViewModel(IRelayCommand activateCommand, IRelayCommand closeCommand) : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    /// <summary>
    /// Resource key from <c>ImageResources.xaml</c> — e.g. <c>StatusSuccessImage</c> for
    /// per-file completions, <c>PackageClosedImage</c> for package summaries.
    /// </summary>
    [ObservableProperty]
    public partial string IconKey { get; set; } = "StatusSuccessImage";
    public IRelayCommand ActivateCommand { get; } = activateCommand;

    public IRelayCommand CloseCommand { get; } = closeCommand;
}
