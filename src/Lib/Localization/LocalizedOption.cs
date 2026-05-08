// <copyright file="LocalizedOption.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;

namespace CSUploader.Lib.Localization;

/// <summary>
/// ComboBox row for <c>SelectedValuePath="Value", DisplayMemberPath="Label"</c> bindings
/// where the displayed label needs to update live when the UI culture changes. The label
/// is fetched through <see cref="Localizer.Instance"/> on every read; the row subscribes
/// to the localiser's <see cref="INotifyPropertyChanged"/> signal so WPF re-renders the
/// drop-down item without rebuilding the whole array.
/// </summary>
public sealed class LocalizedOption<T> : INotifyPropertyChanged
{
    public T Value { get; }

    public string LabelKey { get; }

    public string Label => Localizer.Instance[LabelKey];

    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizedOption(T value, string labelKey)
    {
        Value = value;
        LabelKey = labelKey;
        Localizer.Instance.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, LabelChangedArgs);
    }

    private static readonly PropertyChangedEventArgs LabelChangedArgs = new(nameof(Label));
}
