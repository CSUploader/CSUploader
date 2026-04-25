// <copyright file="SuppressedConfirmationItem.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;

namespace CSUploader.ViewModels;

/// <summary>
/// Row in the Settings page showing one confirmation prompt and whether the user has
/// opted out of it. Toggling <see cref="AskAgain"/> from false → true removes the
/// suppression so the prompt fires next time.
/// </summary>
public partial class SuppressedConfirmationItem : ObservableObject
{
    public SuppressedConfirmationItem(string key, string label, bool askAgain)
    {
        Key = key;
        Label = label;
        this.askAgain = askAgain;
    }

    public string Key { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool askAgain;
}
