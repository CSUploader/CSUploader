// <copyright file="LocExtension.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows.Data;
using System.Windows.Markup;

namespace CSUploader.Lib.Localization;

/// <summary>
/// XAML markup extension that resolves a localised string by key. Use as
/// <c>{loc:Loc Common_OK}</c> on any string-typed property (Content, Header, Text,
/// ToolTip, Title, …). The extension synthesises a one-way binding to
/// <see cref="Localizer.Instance"/>'s indexer, so when the active culture changes
/// every bound value re-evaluates and the UI updates in place.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        Binding binding = new($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
