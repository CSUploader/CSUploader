// <copyright file="SuppressedConfirmationItem.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Lib.Localization;

namespace CSUploader.ViewModels;

/// <summary>
/// Row in the Settings page showing one confirmation prompt and whether the user has
/// opted out of it. Toggling <see cref="AskAgain"/> from false → true removes the
/// suppression so the prompt fires next time. The displayed <see cref="Label"/> is
/// resolved through <see cref="Localizer"/> so language switches re-render the row.
/// </summary>
public partial class SuppressedConfirmationItem : ObservableObject
{
    public SuppressedConfirmationItem(string key, string labelResourceKey, bool askAgain)
    {
        Key = key;
        LabelResourceKey = labelResourceKey;
        AskAgain = askAgain;
        Localizer.Instance.PropertyChanged += (_, _) => OnPropertyChanged(LabelChangedArgs);
    }

    public string Key { get; }

    public string LabelResourceKey { get; }

    public string Label => Localizer.Instance[LabelResourceKey];

    [ObservableProperty]
    public partial bool AskAgain { get; set; }

    private static readonly PropertyChangedEventArgs LabelChangedArgs = new(nameof(Label));
}
