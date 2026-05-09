// <copyright file="DefaultToastWindowFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.ViewModels;
using CSUploader.Views;

namespace CSUploader.Services;

public sealed class DefaultToastWindowFactory : IToastWindowFactory
{
    public IToastHost Create(ToastViewModel viewModel)
    {
        ToastWindow window = new(viewModel);
        return new ToastWindowHost(window);
    }
}
