// <copyright file="AvaloniaToastWindowFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.ViewModels;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of IToastWindowFactory — builds a ToastWindow behind an AvaloniaToastHost,
/// the mirror of the WPF head's DefaultToastWindowFactory.
/// </summary>
public sealed class AvaloniaToastWindowFactory : IToastWindowFactory
{
    public IToastHost Create(ToastViewModel viewModel)
    {
        ToastWindow window = new(viewModel);
        return new AvaloniaToastHost(window);
    }
}
